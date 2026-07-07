package queue

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"strconv"
	"time"

	"github.com/redis/go-redis/v9"
)

// RedisQueue stores jobs in Redis using BullMQ-style keys:
//
//	bull:<queue>:id        counter for job ids
//	bull:<queue>:<jobId>   job hash (name, data, opts, timestamp, attemptsMade, ...)
//	bull:<queue>:wait      list of waiting job ids (LPUSH producer side)
//	bull:<queue>:active    list of in-flight job ids
//	bull:<queue>:completed sorted set of finished job ids (score = finishedOn ms)
//	bull:<queue>:failed    sorted set of failed job ids
//
// This is the BullMQ data-model subset, NOT yet the full BullMQ protocol
// (no Lua scripts, meta key, events stream, delayed/prioritized sets, locks).
// See docs/decisions/0003-bullmq-queue-compatibility.md for the honest status.
type RedisQueue struct {
	client *redis.Client
	prefix string
}

// NewRedis creates a Redis-backed queue with the BullMQ default "bull" prefix.
func NewRedis(client *redis.Client) *RedisQueue {
	return &RedisQueue{client: client, prefix: "bull"}
}

func (q *RedisQueue) key(queueName, suffix string) string {
	return q.prefix + ":" + queueName + ":" + suffix
}

func (q *RedisQueue) jobKey(queueName, jobID string) string {
	return q.prefix + ":" + queueName + ":" + jobID
}

// Enqueue writes the job hash and pushes its id onto the wait list.
func (q *RedisQueue) Enqueue(ctx context.Context, queueName, jobName string, data any) (string, error) {
	payload, err := json.Marshal(data)
	if err != nil {
		return "", err
	}
	id, err := q.client.Incr(ctx, q.key(queueName, "id")).Result()
	if err != nil {
		return "", err
	}
	jobID := strconv.FormatInt(id, 10)

	pipe := q.client.TxPipeline()
	pipe.HSet(ctx, q.jobKey(queueName, jobID), map[string]any{
		"name":         jobName,
		"data":         string(payload),
		"opts":         "{}",
		"timestamp":    time.Now().UnixMilli(),
		"attemptsMade": 0,
	})
	pipe.LPush(ctx, q.key(queueName, "wait"), jobID)
	if _, err := pipe.Exec(ctx); err != nil {
		return "", err
	}
	return jobID, nil
}

// Process moves ids wait -> active with BLMOVE, runs the handler, then records
// the outcome in the completed/failed sorted sets.
func (q *RedisQueue) Process(ctx context.Context, queueName string, handler Handler) error {
	waitKey := q.key(queueName, "wait")
	activeKey := q.key(queueName, "active")

	for {
		if err := ctx.Err(); err != nil {
			return err
		}
		jobID, err := q.client.BLMove(ctx, waitKey, activeKey, "RIGHT", "LEFT", 5*time.Second).Result()
		if errors.Is(err, redis.Nil) {
			continue // poll timeout, no job
		}
		if err != nil {
			if ctx.Err() != nil {
				return ctx.Err()
			}
			slog.Error("queue: blmove failed", "queue", queueName, "error", err)
			time.Sleep(time.Second)
			continue
		}

		fields, err := q.client.HGetAll(ctx, q.jobKey(queueName, jobID)).Result()
		if err != nil {
			slog.Error("queue: cannot read job hash", "queue", queueName, "job", jobID, "error", err)
			continue
		}
		job := &Job{
			ID:    jobID,
			Name:  fields["name"],
			Queue: queueName,
			Data:  json.RawMessage(fields["data"]),
		}

		handlerErr := handler(ctx, job)
		now := time.Now().UnixMilli()

		pipe := q.client.TxPipeline()
		if handlerErr != nil {
			pipe.HSet(ctx, q.jobKey(queueName, jobID), "failedReason", handlerErr.Error(), "finishedOn", now)
			pipe.ZAdd(ctx, q.key(queueName, "failed"), redis.Z{Score: float64(now), Member: jobID})
			slog.Error("queue: job failed", "queue", queueName, "job", jobID, "name", job.Name, "error", handlerErr)
		} else {
			pipe.HSet(ctx, q.jobKey(queueName, jobID), "returnvalue", "null", "finishedOn", now)
			pipe.ZAdd(ctx, q.key(queueName, "completed"), redis.Z{Score: float64(now), Member: jobID})
		}
		pipe.LRem(ctx, activeKey, 1, jobID)
		if _, err := pipe.Exec(ctx); err != nil {
			slog.Error("queue: cannot finalize job", "queue", queueName, "job", jobID, "error", err)
		}
	}
}

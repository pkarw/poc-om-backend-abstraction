// Package queue is the port of upstream's queue abstraction
// (packages/queue, QUEUE_STRATEGY env: local|redis).
//
// The Redis implementation uses BullMQ-style key naming (bull:<queue>:*) as a
// stepping stone towards wire compatibility with Node BullMQ. Full BullMQ
// compatibility status is documented in docs/decisions/0003-bullmq-queue-compatibility.md.
package queue

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/redis/go-redis/v9"
)

// Job is one unit of work pulled from a queue.
type Job struct {
	ID    string
	Name  string
	Queue string
	Data  json.RawMessage
}

// Handler processes a single job. A non-nil error marks the job as failed.
type Handler func(ctx context.Context, job *Job) error

// JobQueue is the strategy interface implemented by the redis and local backends.
type JobQueue interface {
	// Enqueue adds a named job with a JSON-serializable payload and returns its id.
	Enqueue(ctx context.Context, queueName, jobName string, data any) (string, error)
	// Process consumes jobs from queueName until ctx is cancelled. It blocks.
	Process(ctx context.Context, queueName string, handler Handler) error
}

// New picks the backend from QUEUE_STRATEGY. client may be nil for "local".
func New(strategy string, client *redis.Client) (JobQueue, error) {
	switch strategy {
	case "redis":
		if client == nil {
			return nil, fmt.Errorf("queue: redis strategy requires a redis client")
		}
		return NewRedis(client), nil
	case "local":
		return NewLocal(), nil
	default:
		return nil, fmt.Errorf("queue: unknown QUEUE_STRATEGY %q (want local|redis)", strategy)
	}
}

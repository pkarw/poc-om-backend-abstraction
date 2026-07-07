package queue

import (
	"context"
	"encoding/json"
	"strconv"
	"sync"
	"sync/atomic"
)

// LocalQueue is an in-process channel-backed queue, the counterpart of
// upstream's QUEUE_STRATEGY=local. It only works within a single process
// (dev/tests); use the redis strategy whenever api and worker are separate.
type LocalQueue struct {
	mu      sync.Mutex
	chans   map[string]chan *Job
	counter atomic.Int64
}

// NewLocal creates an in-memory queue.
func NewLocal() *LocalQueue {
	return &LocalQueue{chans: make(map[string]chan *Job)}
}

func (q *LocalQueue) channel(queueName string) chan *Job {
	q.mu.Lock()
	defer q.mu.Unlock()
	ch, ok := q.chans[queueName]
	if !ok {
		ch = make(chan *Job, 1024)
		q.chans[queueName] = ch
	}
	return ch
}

// Enqueue pushes the job onto the in-memory channel for the queue.
func (q *LocalQueue) Enqueue(ctx context.Context, queueName, jobName string, data any) (string, error) {
	payload, err := json.Marshal(data)
	if err != nil {
		return "", err
	}
	jobID := strconv.FormatInt(q.counter.Add(1), 10)
	job := &Job{ID: jobID, Name: jobName, Queue: queueName, Data: payload}
	select {
	case q.channel(queueName) <- job:
		return jobID, nil
	case <-ctx.Done():
		return "", ctx.Err()
	}
}

// Process consumes jobs from the in-memory channel until ctx is cancelled.
func (q *LocalQueue) Process(ctx context.Context, queueName string, handler Handler) error {
	ch := q.channel(queueName)
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case job := <-ch:
			_ = handler(ctx, job) // errors are the handler's responsibility to log
		}
	}
}

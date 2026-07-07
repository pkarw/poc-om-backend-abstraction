package queue

import (
	"context"
	"encoding/json"
	"testing"
	"time"
)

func TestRedisQueueUsesBullMQKeyNaming(t *testing.T) {
	q := NewRedis(nil)
	cases := map[string]string{
		q.key("health_check", "wait"):      "bull:health_check:wait",
		q.key("health_check", "active"):    "bull:health_check:active",
		q.key("health_check", "completed"): "bull:health_check:completed",
		q.key("health_check", "failed"):    "bull:health_check:failed",
		q.key("health_check", "id"):        "bull:health_check:id",
		q.jobKey("health_check", "42"):     "bull:health_check:42",
	}
	for got, want := range cases {
		if got != want {
			t.Errorf("key = %q, want %q", got, want)
		}
	}
}

func TestLocalQueueRoundTrip(t *testing.T) {
	q := NewLocal()
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()

	id, err := q.Enqueue(ctx, "health_check", "ping", map[string]string{"source": "test"})
	if err != nil {
		t.Fatalf("Enqueue() error = %v", err)
	}
	if id != "1" {
		t.Errorf("job id = %q, want 1", id)
	}

	got := make(chan *Job, 1)
	procCtx, stop := context.WithCancel(ctx)
	go func() {
		_ = q.Process(procCtx, "health_check", func(_ context.Context, job *Job) error {
			got <- job
			stop()
			return nil
		})
	}()

	select {
	case job := <-got:
		if job.Name != "ping" {
			t.Errorf("job name = %q, want ping", job.Name)
		}
		var payload map[string]string
		if err := json.Unmarshal(job.Data, &payload); err != nil || payload["source"] != "test" {
			t.Errorf("job data = %s (err %v), want source=test", job.Data, err)
		}
	case <-ctx.Done():
		t.Fatal("timed out waiting for job")
	}
}

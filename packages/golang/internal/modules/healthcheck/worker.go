package healthcheck

import (
	"context"
	"log/slog"

	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/queue"
)

// handlePingJob is the module's no-op worker on queue "health_check".
// It validates the payload and logs — the minimal reference for
// upstream's modules/<module>/workers/*.ts.
func handlePingJob(_ context.Context, job *queue.Job) error {
	payload, err := ParsePingPayload(job.Data)
	if err != nil {
		return err
	}
	slog.Info("health_check: ping job processed",
		"job_id", job.ID, "queue", job.Queue, "source", payload.Source)
	return nil
}

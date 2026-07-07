package healthcheck

import (
	"context"
	"encoding/json"
	"log/slog"
	"net/http"
	"time"

	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/registry"
)

type checkResults struct {
	Database bool `json:"database"`
	Redis    bool `json:"redis"`
}

type healthResponse struct {
	Status string       `json:"status"`
	Module string       `json:"module"`
	Checks checkResults `json:"checks"`
}

// handleGetHealthCheck serves GET /api/health_check with real DB and Redis
// pings. On success it also enqueues a no-op "ping" job (exercising the queue)
// and publishes the health_check.pinged event (exercising the bus).
func handleGetHealthCheck(deps *registry.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
		defer cancel()

		checks := checkResults{}

		var pingCount int
		if err := deps.DB.QueryRow(ctx, "SELECT count(*) FROM om_health_ping").Scan(&pingCount); err == nil {
			checks.Database = true
		} else {
			slog.Warn("health_check: database check failed", "error", err)
		}

		if err := deps.Redis.Ping(ctx).Err(); err == nil {
			checks.Redis = true
		} else {
			slog.Warn("health_check: redis check failed", "error", err)
		}

		if checks.Database && checks.Redis {
			payload := PingPayload{Source: "api"}
			if _, err := deps.Queue.Enqueue(ctx, QueueName, "ping", payload); err != nil {
				slog.Warn("health_check: enqueue failed", "error", err)
			}
			_ = deps.Events.Publish(ctx, EventPinged, payload)
		}

		status, code := "ok", http.StatusOK
		if !checks.Database || !checks.Redis {
			status, code = "error", http.StatusServiceUnavailable
		}

		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(code)
		_ = json.NewEncoder(w).Encode(healthResponse{
			Status: status,
			Module: ModuleID,
			Checks: checks,
		})
	}
}

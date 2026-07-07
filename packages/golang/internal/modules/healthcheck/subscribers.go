package healthcheck

import (
	"context"
	"encoding/json"
	"log/slog"
)

// onPinged reacts to the health_check.pinged event — the minimal reference
// for upstream's modules/<module>/subscribers/*.ts.
func onPinged(_ context.Context, payload json.RawMessage) error {
	p, err := ParsePingPayload(payload)
	if err != nil {
		return err
	}
	slog.Info("health_check: pinged event received", "source", p.Source)
	return nil
}

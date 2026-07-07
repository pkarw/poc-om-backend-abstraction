package healthcheck

import (
	"encoding/json"

	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/validation"
)

// PingPayload is the job/event payload — the Go counterpart of a Zod schema
// (upstream: modules/<module>/data/validators.ts).
type PingPayload struct {
	Source string `json:"source" validate:"required"`
}

// ParsePingPayload unmarshals and validates a raw JSON payload.
func ParsePingPayload(raw json.RawMessage) (*PingPayload, error) {
	var p PingPayload
	if err := json.Unmarshal(raw, &p); err != nil {
		return nil, err
	}
	if err := validation.Struct(&p); err != nil {
		return nil, err
	}
	return &p, nil
}

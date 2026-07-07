package healthcheck

import (
	"encoding/json"
	"testing"
)

func TestParsePingPayload(t *testing.T) {
	p, err := ParsePingPayload(json.RawMessage(`{"source":"api"}`))
	if err != nil {
		t.Fatalf("ParsePingPayload() error = %v", err)
	}
	if p.Source != "api" {
		t.Errorf("Source = %q, want api", p.Source)
	}
}

func TestParsePingPayloadRejectsMissingSource(t *testing.T) {
	if _, err := ParsePingPayload(json.RawMessage(`{}`)); err == nil {
		t.Fatal("ParsePingPayload({}) should fail validation")
	}
}

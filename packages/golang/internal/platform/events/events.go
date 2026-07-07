// Package events is a minimal in-process event bus, the counterpart of
// upstream's packages/events local strategy. Modules contribute subscribers
// (upstream: modules/<module>/subscribers/*.ts). A Redis-backed distributed
// strategy is an explicit porting task (see AGENTS.md, Queues & Events).
package events

import (
	"context"
	"encoding/json"
	"log/slog"
	"sync"
)

// Handler receives the JSON-encoded event payload.
type Handler func(ctx context.Context, payload json.RawMessage) error

// Bus is a threadsafe in-process publish/subscribe hub.
type Bus struct {
	mu   sync.RWMutex
	subs map[string][]Handler
}

// New creates an empty bus.
func New() *Bus {
	return &Bus{subs: make(map[string][]Handler)}
}

// Subscribe registers a handler for an event name (e.g. "health_check.pinged").
func (b *Bus) Subscribe(event string, h Handler) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.subs[event] = append(b.subs[event], h)
}

// Publish delivers the payload to all subscribers synchronously.
// Subscriber errors are logged, never propagated to the publisher.
func (b *Bus) Publish(ctx context.Context, event string, payload any) error {
	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	b.mu.RLock()
	handlers := b.subs[event]
	b.mu.RUnlock()
	for _, h := range handlers {
		if err := h(ctx, data); err != nil {
			slog.Error("events: subscriber failed", "event", event, "error", err)
		}
	}
	return nil
}

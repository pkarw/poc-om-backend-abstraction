// Package redisconn provides the shared Redis client (go-redis/v9).
package redisconn

import (
	"context"

	"github.com/redis/go-redis/v9"
)

// Connect parses a redis:// URL, opens a client and verifies connectivity.
func Connect(ctx context.Context, redisURL string) (*redis.Client, error) {
	opts, err := redis.ParseURL(redisURL)
	if err != nil {
		return nil, err
	}
	client := redis.NewClient(opts)
	if err := client.Ping(ctx).Err(); err != nil {
		_ = client.Close()
		return nil, err
	}
	return client, nil
}

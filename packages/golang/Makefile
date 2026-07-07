.PHONY: up down dev worker migrate test tidy

up: ## Build and start postgres, redis, api, worker
	docker compose up --build -d

down: ## Stop everything and drop volumes
	docker compose down -v

dev: ## Run the API natively (Go recompiles fast; re-run after changes)
	go run ./cmd/api

worker: ## Run the queue worker natively
	go run ./cmd/worker

migrate: ## Apply DB migrations natively
	go run ./cmd/migrate

test: ## Run tests
	go test ./...

tidy: ## Resolve dependencies and generate go.sum
	go mod tidy

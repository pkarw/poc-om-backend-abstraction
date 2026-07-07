.PHONY: up down dev worker migrate test

up:
	docker compose up --build -d

down:
	docker compose down -v

dev:
	dotnet watch --project src/OpenMercato.Api run

worker:
	dotnet run --project src/OpenMercato.Worker

migrate:
	dotnet ef database update --project src/OpenMercato.Api --startup-project src/OpenMercato.Api

test:
	dotnet test OpenMercato.sln

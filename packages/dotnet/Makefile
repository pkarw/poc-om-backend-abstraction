.PHONY: up down dev worker migrate test init greenfield seed cli

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

# Global CLI (OpenMercato.Cli). Mirrors upstream `mercato <command>`.
# init      : migrate + seed the initial Acme tenant (== upstream `mercato init` / `yarn initialize`).
# greenfield: DROP + recreate schema, migrate, seed (== upstream `dev:greenfield`; requires --yes).
# seed      : run the OM-parity seeder only (idempotent).
# cli       : passthrough, e.g. `make cli ARGS="add-user --email a@b.c --password Secret1! --organizationId <id>"`.
init:
	dotnet run --project src/OpenMercato.Cli -- init

greenfield:
	dotnet run --project src/OpenMercato.Cli -- greenfield --yes

seed:
	dotnet run --project src/OpenMercato.Cli -- seed

cli:
	dotnet run --project src/OpenMercato.Cli -- $(ARGS)
</content>

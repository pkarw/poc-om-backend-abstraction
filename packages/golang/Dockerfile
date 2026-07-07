# Multi-stage build: one image serves both api and worker (different command).
FROM golang:1.23-alpine AS build
WORKDIR /src
COPY . .
# go.sum is not committed; tidy resolves and pins the full module graph.
RUN go mod tidy
RUN CGO_ENABLED=0 go build -trimpath -o /out/api ./cmd/api \
 && CGO_ENABLED=0 go build -trimpath -o /out/worker ./cmd/worker \
 && CGO_ENABLED=0 go build -trimpath -o /out/migrate ./cmd/migrate

FROM alpine:3.20
RUN adduser -D -u 10001 app
WORKDIR /app
COPY --from=build /out/api /out/worker /out/migrate /usr/local/bin/
COPY migrations ./migrations
USER app
EXPOSE 8090
CMD ["api"]

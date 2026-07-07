# 0003 — FluentValidation as the Zod Equivalent

## Status

Accepted

## Context

Upstream validates request bodies with Zod schemas in each module's
`data/validators.ts` and returns 400 with field-level error details on failure.
The port may use a better idiomatic solution when observable behavior stays
identical.

## Decision

Use FluentValidation 11. Each ported Zod schema becomes an
`AbstractValidator<TRequest>` in the module's `Validators/` folder (see
`PingRequestValidator`). Handlers validate explicitly and return
`400 {"error":"validation_failed","details":[{"field","message"}]}` on failure,
keeping the error contract stable across all modules.

## Consequences

- Zod rule translation is mostly mechanical (`z.string().min(1).max(200)` ->
  `RuleFor(x => x.Source).NotEmpty().MaximumLength(200)`).
- Validation is invoked explicitly in handlers (no auto-validation middleware),
  which keeps the request pipeline transparent and matches upstream handlers
  calling `schema.parse()` themselves.
- Exact upstream error-body shapes must be checked per endpoint during porting;
  the scaffold's shape is the default, not a guarantee of byte compatibility.

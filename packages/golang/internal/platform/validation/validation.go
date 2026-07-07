// Package validation is the Go equivalent of upstream's Zod validators
// (packages/core/src/modules/<module>/data/validators.ts).
// Modules define plain structs with `validate` tags and call Struct().
package validation

import "github.com/go-playground/validator/v10"

var validate = validator.New()

// Struct validates any struct using its `validate` tags.
func Struct(s any) error {
	return validate.Struct(s)
}

// Validator exposes the shared validator instance for custom registrations.
func Validator() *validator.Validate {
	return validate
}

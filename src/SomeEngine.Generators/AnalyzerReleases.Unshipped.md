; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
SEBC012 | SomeEngine.Serialization | Error | Explicit field order must identify every serialized field once.
SEBC013 | SomeEngine.Serialization | Error | Text encoding requires supported shapes and static nested codecs.
SEAS001 | SomeEngine.Assets | Error | Independent asset declarations require the shared identity prefix and direct properties.
SEAS002 | SomeEngine.Assets | Error | Shared asset values must exclude mutable reference graphs.
SEAB001 | SomeEngine.Assets | Error | Block declarations require explicit independently encoded fields.
SEAB002 | SomeEngine.Assets | Error | Shared block values must exclude mutable reference graphs.
SEAC001 | SomeEngine.Assets | Error | Import capabilities must be accessible through direct typed factories.
SEAC002 | SomeEngine.Assets | Error | Persistent capability identities must not conflict.
SEAC003 | SomeEngine.Assets | Error | Capability registration requires a public static partial entry point.
SEAC004 | SomeEngine.Assets | Error | Importers require parameterless constructors and read settings through the import context.
SEAU001 | SomeEngine.Assets | Error | Explicit use objects cannot retain or expose asset access types.

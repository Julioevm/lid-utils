# Contributing setting definitions

`settings.catalog.json` adds human-readable metadata to values that remain stored in
Let It Die's database. It never contains game data and it is not an instruction to
write to the database.

## Identity and versioning

The root `schemaVersion` describes the JSON format and is currently `1`.
`catalogVersion` identifies the content revision and should change whenever a
definition changes. A setting is uniquely identified by the exact combination of
`sourceTable` and `key`; duplicate combinations are rejected.

Supported source tables and matching value types are:

| sourceTable | valueType |
| --- | --- |
| `master_const_int` | `integer` |
| `master_const_float` | `float` |
| `master_const_str` | `string` |

## Fields

Every definition includes `label`, `description`, `category`, `valueType`,
`rawUnits`, and `risk`. Risk is one of `low`, `moderate`, `high`, or
`experimental`. Optional numeric metadata includes `minimum`, `maximum`, and a
positive `step`. Integer definitions must use whole numbers for these fields.

`defaultDisplayFormat` is a .NET numeric format such as `0`, `0.##`, or `P1`.
Strings do not use numeric fields or conversions.

A display conversion is optional and has this form:

```json
"displayUnits": "percent",
"conversion": { "kind": "scaleOffset", "scale": 100, "offset": 0 }
```

The interface calculates `display = raw * scale + offset`. The original raw text
is retained and shown separately. A conversion requires `displayUnits`; its scale
must be finite and non-zero.

## Review checklist

1. Verify the exact table, key, and type against a legally obtained local database.
2. Explain observed behavior without presenting guesses as facts.
3. Use `experimental` until the behavior and safe range are reproducible.
4. Do not add purchases, DLC, entitlements, online schedules, or service settings.
5. Run `dotnet test LidUtils.sln`—catalog duplicates, invalid ranges, incompatible
   types, conversions, and values fail with setting-specific messages.

Definitions missing from a particular database revision produce a warning rather
than hiding the database's undocumented constants. New JSON fields require a new
schema version because unknown fields are deliberately rejected.

# Cairn.Templates

`dotnet new` templates for [Cairn](https://github.com/JonahLargen/Cairn) — opt-in HATEOAS for
ASP.NET Core.

## Install

```bash
dotnet new install Cairn.Templates
```

## Create a project

```bash
dotnet new cairn-api -o Orders.Api
cd Orders.Api
dotnet run
```

This scaffolds an ASP.NET Core minimal API already wired for hypermedia: clean DTOs with their link
rules declared separately in `LinkConfig<T>` classes, a state-conditional `cancel` affordance,
compile-checked routes via the generated `Routes.*` catalog, an embedded collection, and a HAL-FORMS
`create` form. Start at `/`, or — with the explorer enabled (the default) — browse the API
interactively at `/explorer`.

## Options

| Option | Default | Description |
| --- | --- | --- |
| `-f`, `--framework` | `net10.0` | Target framework: `net10.0`, `net9.0`, or `net8.0` (LTS). |
| `--explorer` | `true` | Include the browsable HAL Explorer at `/explorer` (Development only). |
| `--openapi` | `false` | Document the hypermedia in Swagger (`Cairn.Swashbuckle`) and serve Swagger UI in Development. |

```bash
# An LTS project without the explorer, with Swagger docs:
dotnet new cairn-api -o Orders.Api -f net8.0 --explorer false --openapi true
```

## Learn more

- [Documentation](https://jonahlargen.github.io/Cairn)
- [Getting started](https://jonahlargen.github.io/Cairn/articles/getting-started.html)
- [Packages](https://jonahlargen.github.io/Cairn/articles/packages.html)

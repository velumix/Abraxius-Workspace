# AXL language specification (implemented subset)

## Purpose

AXL represents machine intent, references, capability calls, evidence relationships, results, verification, and execution dependencies. It is intentionally not a general-purpose programming language.

## Document

Every document begins with `axl/1` (or an explicitly supported minor version such as `axl/1.0`). A document contains one command or a `batch { ... }` of commands. Whitespace is insignificant except inside strings. Command order is not execution order; dependencies are explicit.

Examples:

```text
axl/1 find code q="ExecutionGraph" lim=20
```

```text
axl/1
batch {
  c#1 find code q="scheduler"
  c#2 call @cap:git.status
  c#3 synth obj="combine" dep=[c#1 c#2]
}
```

## Values

The typed IR supports text, signed and unsigned integers, decimal values, booleans, null, references, identifiers, lists, and records. The command schemas currently use a deliberately small subset of these values. Canonical booleans are `true` and `false`; canonical absence is `null`.

Strings use double quotes with `\\`, `\"`, `\n`, `\r`, `\t`, and `\uXXXX` escapes. Triple-quoted blocks are accepted for larger text values. Unknown escapes are errors.

## Commands

The built-in schemas are:

| Command | Required | Optional | Runtime meaning |
| --- | --- | --- | --- |
| `find code` | `q` | `lim`, `scope` | phase-2 `code.search` tool work |
| `call` | capability positional or `cap` | `op`, `target`, `mutation`, parameters | typed capability request |
| `memory query` | `q` | `lim`, `scope` | memory work descriptor |
| `synth` | none | `obj`, `dep` | synthesis work depending on references |
| `verify` | none | `obj`, `dep`, `profile` | verification work |
| `intent` | `obj` | `pri`, `attrs` | phase-2 intent |
| `delegate` | `agent`, `obj` | `ev`, `mode` | model/delegation work |
| `ret` | `ref`, `status` | `ev`, `err` | protocol observation; not executable |
| `state` | `ref`, `status` | none | protocol state observation; not executable |

`call` allows extension parameters because capability schemas are supplied by the registered capability boundary. The default validator still rejects mutation unless its policy explicitly enables it.

## References

`c#`, `t#`, `r#`, `e#`, and `a#` identify commands, tasks, results, evidence, and artifacts. Namespaced references use `@cap:`, `@agent:`, `@model:`, `@secret:`, and `@concept:`. `@project` is the built-in project reference.

Reference resolution and authorization are runtime responsibilities. A syntactically valid `@cap:file.delete` is not permission to call it.

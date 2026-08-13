# AXL grammar

This EBNF describes the implemented core grammar. Schema-specific command fields are validated after parsing.

```ebnf
document       = header, whitespace, (batch | command), whitespace ;
header         = "axl/", number, [ ".", number ] ;
batch          = "batch", whitespace, "{", { command }, "}" ;
command        = [ command-id, whitespace ], command-name, { whitespace, argument } ;
command-id     = "c#", identifier ;
command-name   = identifier, [ whitespace, identifier ] ;
argument       = reference-argument | named-argument | list | record ;
reference-argument = reference ;
named-argument = identifier, "=", value ;
value          = string | block-string | number | boolean | null | reference |
                 identifier | list | record ;
list           = "[", [ value, { [ "," ], whitespace, value } ], "]" ;
record         = "{", [ named-argument, { [ "," ], whitespace, named-argument } ], "}" ;
reference      = "@project" | "@", identifier, ":", identifier |
                 ( ( "c#" | "t#" | "r#" | "e#" | "a#" ), identifier ) ;
string         = '"', { escaped-character | unicode-character }, '"' ;
block-string   = '"""', { unicode-character }, '"""' ;
number         = [ "-" ], digit, { digit }, [ ".", digit, { digit } ] ;
boolean        = "true" | "false" ;
null           = "null" ;
identifier     = identifier-start, { identifier-part } ;
identifier-start = letter | "_" ;
identifier-part  = letter | digit | "_" | "-" | "." | "/" | ":" | "#" | "@" ;
```

The lexer also recognizes commas and uses them as optional list/record separators. Runtime parsing is strict about malformed escapes, duplicate fields, invalid UTF-8, limits, and unknown core command names. The grammar is intentionally not a full expression grammar: there are no loops, functions, operators, interpolation, or shell escapes.

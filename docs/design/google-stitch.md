# Google Stitch provider

The initial adapter uses Google Stitch's documented Streamable HTTP MCP
endpoint. It calls the typed MCP operations needed by Abraxius:

```text
list_projects
create_project
generate_screen_from_text
generate_variants
edit_screens
list_screens
get_screen
```

The adapter uses `X-Goog-Api-Key` or `Authorization: Bearer` headers through
the Phase 17 Secret Broker. It does not use Node, JavaScript, a shell helper,
or generated HTML as application source. Markup is retained as a design
reference in the Design Artifact.

Configure an existing development credential with
`ABRAXIUS_STITCH_API_KEY` or `ABRAXIUS_STITCH_ACCESS_TOKEN`. The optional
`ABRAXIUS_STITCH_PROJECT` pins the provider project; otherwise Abraxius reuses
or creates the stable `Abraxius Workspace` project.

The provider adapter is deliberately replaceable. A local or future provider
can implement `IDesignGenerationProvider` without changing Chat, Artifact,
Security, or mission code.

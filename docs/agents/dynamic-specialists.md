# Dynamic specialists

`SpecialistFactory` derives an ephemeral domain specialist from a validated template. Effective capabilities are the intersection of template capabilities and requested capabilities; model output cannot add privileges. Definitions must pass the same registry validation as built-ins and use a distinct namespace/ID.

Built-ins are protected from accidental replacement. Legacy display aliases are migration-only and do not alter semantic roles.

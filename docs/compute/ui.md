# Compute UI

The Avalonia Compute surface uses MVVM and compiled item bindings. It shows actual devices, unknown values, models, residency, reservations, and backend health. Hardware probing is asynchronous and a complete snapshot is coalesced onto the UI thread.

Lists use virtualizing controls. The surface avoids decorative graphs and does not expose every backend flag as a primary setting.

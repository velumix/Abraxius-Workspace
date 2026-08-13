# Progression UI

Progression is a first-class rail destination with responsive secondary navigation: Overview, Specialists, Skills, Achievements, Prestige, and Career. The surface uses MVVM bindings and observes snapshots; it never calculates XP.

Lists use Avalonia `ListBox` virtualization. The desktop overview can show summary and mastery together, while secondary navigation wraps for narrow displays. Motion is restrained and inherits the workstation reduced-motion policy. Runtime failure colors retain their existing semantic meaning.

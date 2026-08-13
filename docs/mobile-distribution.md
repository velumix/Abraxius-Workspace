# Mobile and browser distribution

Android and iOS do not use the desktop self-updater.

| Target | Owner | Update path | Current repository status |
| --- | --- | --- | --- |
| Android Play | Google Play | Play flexible/immediate update | project exists; workload/store signing required |
| Android sideload | Android package installer | explicit signed APK flow | signing/install flow is a release extension, not a silent updater |
| iOS | App Store/TestFlight/authorized regional distribution | Apple update mechanism | project exists; Apple runner/signing required |
| Browser/WASM | web host/CDN | deployment + cache invalidation/refresh | browser project builds in the solution |

Android updates must retain application ID, signing identity, and monotonic version code. iOS
updates must use Apple-authorized distribution. Browser deployments should surface a refresh prompt
when a new frontend is active while preserving remote execution state where possible.

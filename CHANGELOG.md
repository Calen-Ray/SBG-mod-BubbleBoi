# Changelog

## 0.2.2

- Bump the default `Visuals.ExpiryWarningSeconds` window from 2 → 3.5 s so the orange→red
  hue ramp gives the wearer a more useful heads-up before the shield drops. Existing
  configs still honour their saved value; delete the line under `[Visuals]` to pick up the
  new default.

## 0.2.1

- Fix shielded players getting stuck wading from sand into shallow water. The 0.2.0 sand-fix
  bailed on every vanilla-found ground; now it only bails when that ground is at or above
  the water surface, and re-enables synthetic water-walk when vanilla snapped to a submerged
  sand floor.
- Fix the expiry hue ramp not actually showing on the bubble shield. The shield visual is a
  `ParticleSystemRenderer`, which ignores `MaterialPropertyBlock` color overrides; now sets
  `ParticleSystem.MainModule.startColor` directly and restores the original on reset.

## 0.2.0

- Fix shielded players getting stuck in sand (and occasionally other terrain) when exiting a
  water hazard. The synthetic-ground postfix now leaves real terrain alone whenever the
  vanilla ground check already found a contact, so shore terrain isn't replaced by our
  `NotTerrain` water proxy.
- Add an expiry warning hue: the local player's bubble shield ramps from its base color
  through orange and into red over the configured trailing window (default 2 s) so the
  wearer gets a visual countdown before the shield drops. Configurable under `Visuals`.
- Silence per-frame water-walk decision logs by default. Flip `Diagnostics.VerboseLogging`
  to `true` to bring them back when reporting an issue.

## 0.1.1

- Refine water-surface walking so shielded players consistently rise to and walk on the water surface.
- Add targeted debug logging for shield and water-surface decisions.
- Replace the package icon with the final cover art.

## 0.1.0

- Initial BubbleBoi release.
- Players can walk on water while the electromagnet shield is active.
- Normal water behavior resumes immediately when the shield expires.

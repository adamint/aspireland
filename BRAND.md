# Aspireland Transit

Aspireland is a simulated transit network, not a real transit authority. Use **Aspireland Transit** for the public identity, **Aspireland** in conversation, and **Dispatch** for the operator workspace. Use Aspire, not “.NET Aspire”, when referring to the underlying platform.

## Palette

These are the canonical [Aspire brand kit](https://microsoft.github.io/aspire-brand/) values, linked from [aspire.dev](https://aspire.dev/).

| Color | Hex | CSS token |
| --- | --- | --- |
| Primary purple | `#7455DD` | `--aspire-primary` |
| Deeper purple | `#512BD4` | `--aspire-purple` |
| Secondary purple | `#B9AAEE` | `--aspire-secondary` |
| Muted purple | `#DCD5F6` | `--aspire-muted` |
| Grey | `#DCE0E8` | `--aspire-grey` |
| Black | `#1F1E33` | `--aspire-black` |
| White | `#FFFFFF` | `--aspire-white` |

`web/src/brand.css` owns the tokens. Page surfaces use off-white `#F7F6FB`; a quiet purple tint `#F0ECFB` groups controls. Body text uses black, secondary text `#5B586F`, and field boundaries `#847E9B`. Use white on deeper purple for primary actions and selected controls. Primary purple identifies the mark; do not substitute the lighter purples behind white body text.

Blue `#4B8FF5`, Amber `#F0AC32`, Green `#46B887`, and AX `#BD88ED` identify actual routes. Keep their names, map patterns, paths and markers. They are not accent colors to spread across unrelated controls. In particular, Green means the Green line, not a decorative “live” badge. Alerts and failures retain explicit text and semantic warning/error colors.

## Type and mark

Use **Poppins**, self-hosted through `@fontsource/poppins`: 400 for body text, 500 for secondary labels, 600 for controls, and 700 for headings and wayfinding. Use tabular numbers for arrivals and operational data. Do not fetch fonts from a CDN.

The original mark is an angular **A** with two converging tracks, a crossbar and a central junction stem. White strokes sit inside a primary-purple square with a 4-unit corner radius on a 48-unit grid. `web/public/favicon.svg` is the single asset used by the favicon and `TransitMark`; do not redraw it separately. The mark is decorative beside readable product text. Keep at least 8px clear space, display at 32px or larger in the UI, and do not stretch, rotate or add effects. The favicon may render smaller.

This is not the WMATA mark or an official Aspire product logo. Do not copy transit-authority marks or imply endorsement or real-world service.

## Components

- Use a 4px spacing unit: 4, 8, 12, 16 and 24px. Keep the existing compact, trip-first layout; identity should not push the mobile journey form below the first viewport.
- Use square transit signage, 4px corners and bold, direct labels. Keep headings sentence case; reserve uppercase for short signs and route identifiers.
- Share the mark, palette and type across passenger and operator headers, navigation, controls, map chrome and legends, arrival boards and assistants. Dense operator data can stay compact without becoming a different brand.
- Keep data surfaces flat: white/off-white, near-black text and quiet borders. No global glow, decorative gradients or animated connection badges.
- Keep normal controls at least 44px tall; preserve explicit map zoom controls and keyboard alternatives. Use a visible 3px purple focus outline, native form semantics and text labels. Never use color alone for service state, route identification or approval.
- Preserve local-time labels, “Terminates here” boards, ordinary journey planning, text/voice assistance and explicit operator approval. Branding must not change service behavior.

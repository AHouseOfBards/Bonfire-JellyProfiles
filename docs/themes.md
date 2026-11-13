# Bonfire themes

A theme is one JSON file. Bonfire's web client draws its screens from it, and any other
app can draw its switcher from the same file. The web client gets no styling the file
doesn't describe, so another app that follows the file looks the same.

- Built-in themes are in `Web/themes/`, one file each.
- Administrators make more on the settings page, or import a file.
- `GET /plugins/profiles/themes` returns all of them. See [Getting themes](#getting-themes).

Schema version 1. This document is the reference for it.

## Contents

- [What an app must follow](#what-an-app-must-follow)
- [The file](#the-file)
- [Colors](#colors)
- [Background](#background)
- [Shape](#shape)
- [Type](#type)
- [Depth and focus](#depth-and-focus)
- [Parts](#parts)
- [Layout](#layout)
- [Decorations](#decorations)
- [Where each value is used](#where-each-value-is-used)
- [Screenshots](#screenshots)
- [Rules every theme passes](#rules-every-theme-passes)
- [Getting themes](#getting-themes)
- [Moonfin theme files](#moonfin-theme-files)
- [Changes to this format](#changes-to-this-format)

## What an app must follow

| Part | Must follow | Notes |
| --- | --- | --- |
| `colors`, `background` | Yes | |
| `shape` | Yes | |
| `parts` | Yes, its colors and shapes | Its type and depth values are optional. |
| `layout` | Yes | |
| `type` | No | Use it when you can load the font. Otherwise use `type.font.fallback`. |
| `depth`, `focus` | No | Platforms draw shadows and focus differently. Get close. |
| `decorations` | No | Draw the ones you support. Skip the rest. |

An app that follows only the required parts still looks like the theme.

## The file

```json
{
  "schema": 1,
  "id": "harvest",
  "revision": 4,
  "scheme": "dark",
  "name": { "key": "theme.harvest" },
  "aliases": [],
  "colors": { },
  "background": { },
  "shape": { },
  "type": { },
  "depth": { },
  "focus": { },
  "parts": { },
  "layout": { },
  "decorations": [ ]
}
```

| Field | Type | Description |
| --- | --- | --- |
| `schema` | integer | `1`. |
| `id` | string | Lowercase letters, digits and hyphens. Never changes once published. |
| `revision` | integer | Goes up whenever the file changes. Cache on `id` + `revision`. |
| `scheme` | string | `dark` or `light`: whether the theme's text is light on dark, or dark on light. A light theme gets lighter shadows. Nothing in Bonfire's own CSS may paint white on a theme's surface; `tests/js/lightsafe.js` lists each exception and why. |
| `name` | object | `{ "key": "theme.harvest" }` for a built-in, translated through Bonfire's strings. `{ "text": "Our Theme" }` for an administrator's. |
| `aliases` | string[] | Old ids that now mean this theme. A profile set to one of them gets this one. |

**Colors** are `#rrggbb`, or `#rrggbbaa` when see-through. **Lengths** are pixels at the
size a desktop browser draws them; a TV app scales them the way it scales everything else.

## Colors

| Field | Use |
| --- | --- |
| `accent` | Primary buttons, selected things, focus. |
| `accentFocus` | A primary button under the pointer or in focus. |
| `onAccent` | Text and icons on `accent`. |
| `text1`, `text2`, `text3` | Text: main, secondary, faint. |
| `surface1`, `surface2`, `surface3` | Fills in three steps, usually see-through. |
| `border1`, `border2`, `border3` | Lines in three weights. |
| `panel` | An opaque fill, for things that must hide what scrolls under them. |
| `ring` | The ring around a profile image in focus. |
| `name`, `nameFocus` | A profile's name under its image, and in focus. |
| `shadow` | The color of every shadow. |
| `danger` | Errors and things that delete. |
| `warning` | Warnings. |

Every field is required. In a custom theme, `danger` and `warning` can be any color. The
built-in themes keep them red and amber.

Tints of the accent (8% to 60%) aren't in the file. Mix `accent` with transparency
yourself.

## Background

The ground behind the switcher.

```json
"background": {
  "fill": "#2e160b",
  "layers": [
    { "type": "linear", "angle": 165, "stops": [
      { "color": "#1f0f08", "at": 0 }, { "color": "#2e160b", "at": 45 }, { "color": "#42200e", "at": 100 } ] }
  ]
}
```

- `fill` is required. It's the one color to use when you can't draw gradients.
- `layers` is optional. Layers are drawn in order, first on top, like CSS backgrounds.
- `linear`: `angle` in degrees, 0 pointing up, clockwise.
- `radial`: `shape` is `circle` or `ellipse`, and `x`, `y` are the center in percent of
  the screen.
- `stops[].at` is a percent along the gradient.

### Background image

A custom theme can have one image behind the switcher. Built-in themes don't.

```json
"background": {
  "fill": "#121521",
  "image": { "file": "9be1…04.jpg", "dim": 0.8 }
}
```

- `file` is an image the administrator uploaded to this server, at
  `/plugins/profiles/themes/backgrounds/<file>`. PNG, JPEG or WebP.
- `dim` is 0 to 0.95. Draw the image over the fill and layers so it covers the screen,
  then `fill` over it at this opacity.
- Until the image has loaded, or if it fails, draw the theme as if it had none.
- Any `dim` in range can be saved. A new image starts at a dim that keeps the theme's
  text readable on it; after that it's the administrator's choice.
- An exported theme file leaves the image out, because the file is on this server, not
  in the theme; it keeps `"image": { "dim": … }` so an import can say to upload one.
  Importing never brings an image; replacing a theme keeps the one it had. A
  configuration backup restored on another server drops it too.

## Shape

```json
"shape": {
  "radius": { "small": 6, "medium": 10, "large": 14 },
  "avatar": { "kind": "leaf", "radius": 10 },
  "ringWidth": 3,
  "button": { "kind": "rounded", "radius": 10 },
  "input": { "radius": 10 }
}
```

| Field | Values |
| --- | --- |
| `radius` | Corners: `small` for chips and small controls, `medium` for buttons, cards and dialogs, `large` for full-screen surfaces. |
| `avatar.kind` | `circle`, `square`, `rounded` or `leaf`. |
| `avatar.radius` | `rounded`: percent of the width, so the shape holds at any size. `square`: pixels. `leaf`: pixels for the two sharp corners; the other two are fully round. |
| `ringWidth` | Pixels. |
| `button.kind` | `pill`, `rounded` (uses `radius`) or `square`. Every button has the same shape. |
| `input.radius` | Pixels. |

## Type

```json
"type": {
  "font": { "family": "Lora", "fallback": "serif" },
  "title": { "weight": 600, "spacing": 0.01, "scale": 1.05 },
  "name": { "weight": 600, "scale": 1, "opacity": 1 },
  "button": { "case": "none", "spacing": 0.02, "weight": 600 }
}
```

- `font.family` is one of the fonts in `GET /themes`. Bonfire serves Noto Sans, Outfit,
  Inter, Nunito, Montserrat, DM Sans, Atkinson Hyperlegible Next, Lora, Playfair Display,
  Quicksand, Comfortaa, Baloo 2, Manrope, Figtree, Plus Jakarta Sans, Lexend, Oswald,
  Bebas Neue, Righteous, Cinzel, Merriweather, Fraunces, JetBrains Mono and Press Start 2P,
  so you don't need them installed. Each covers every language Bonfire is
  translated into.
- `font.fallback` is `sans-serif`, `serif` or `rounded`. Use it when you can't load the
  family.
- `spacing` is letter spacing in em.
- `scale` multiplies the size the screen already uses.
- `case` is `none` or `upper`.

## Depth and focus

A shadow is `{ "x": 0, "y": 10, "blur": 24, "spread": 0, "opacity": 0.45 }`, in
`colors.shadow` unless it has its own `color`. `null` means none. A shadow with a `color`
uses that color's own alpha and ignores `opacity`. On `depth.title` that is a glow:
`"color": "#10a0f080"` is a blue glow behind the title.

```json
"depth": { "avatar": { }, "title": { }, "sheet": null },
"focus": { "scale": 1.05, "glow": { "x": 0, "y": 14, "blur": 30, "color": "#f0a04b59" } }
```

| Field | Use |
| --- | --- |
| `depth.avatar` | Under a profile image. |
| `depth.title` | Behind the "Who's watching?" title. A text shadow. |
| `depth.sheet` | Under a dialog or form. |
| `focus.scale` | How much a profile grows in focus. `1` is not at all. |
| `focus.glow` | The shadow a profile takes in focus. |

On a TV, focus is what the pointer's hover is on a desktop, so use these for focus.

## Parts

Colors and shapes for the parts every screen is built from. A border is
`{ "width": 1, "style": "solid", "color": "#…" }`, with `style` one of `solid`, `dashed`
or `dotted`. A `line` is just a color, for a 1-pixel solid edge.

| Part | Fields |
| --- | --- |
| `button2` | The second kind of button: `fill`, `text`, `border`, and the same three under `focus`, plus `focus.glow`. The first kind is `accent` on `onAccent`. |
| `addTile` | The "Add profile" tile: `fill`, `border`, `text`, `focusFill`. |
| `card` | A box inside a form: `fill`, `line`. |
| `sheet` | A dialog or form on its own: `fill`, `border`. Its shadow is `depth.sheet`. |
| `input` | A text field: `fill`, `line`. |

## Layout

```json
"layout": { "households": "panels", "profiles": "tiles", "align": "center", "size": "medium", "logo": "top-right" }
```

`households` is `panels`, where each household's profiles sit in a box with a heading, or
`open`, where profiles sit straight on the background. With `open`, a screen with one
household shows no heading at all. It is required.

The other four are optional. Leave one out and it takes the default, the first value below.

| Field | Values | |
| --- | --- | --- |
| `profiles` | `tiles`, `list` | Tiles in rows, or one profile per line with the image and the name beside it. |
| `align` | `center`, `left` | The title, the profiles and the buttons centered, or lined up 5% in from the left. |
| `size` | `medium`, `small`, `large` | The image: 80% or 125% of medium. A list draws smaller images than tiles at every size. |
| `logo` | `top-right`, `top-left`, `bottom-left`, `bottom-right` | The corner the server's logo goes in. At the bottom it comes after the buttons, so on a screen that scrolls it is below them. |

There is no free positioning. Whatever is chosen, the logo never covers the title, a
profile or a button.

## Decorations

Things a theme does that can't be described as colors and shapes. Each has a name from
this list and a few values. Bonfire may add names in later versions, so skip one you
don't know.

| Name | Values | What it draws |
| --- | --- | --- |
| `pattern` | `image`, `size`, `drift`, `opacity` | An image repeated behind the switcher, faint. `image` is a file from `GET /themes`. `size` is one image's width and height in pixels. `drift` is `none`, or `down` with `seconds` (4 to 600) for one image's height. `opacity` is optional, 0.1 to 1, and 1 when left out. |
| `pattern2` | the same as `pattern` | A second pattern, drawn above the first, with its own image, size, drift and opacity. Needs a `pattern`, and a theme has at most one. Without a `pattern2` a theme is exactly as it was. |
| `rings` | `gradients`, `gap` | Each profile gets a gradient ring of its own: profile 1 takes the first, profile 2 the second, and so on, starting over at the end. Not the Add tile. `gap` is the color of the thin space between the ring and the image. |
| `frost` | `blur` | Dialogs blur what's behind them by `blur` pixels, and the second kind of button by two thirds of that. |
| `twinkle` | `seconds` | The pattern fades slowly down and back up, one cycle in `seconds` (2 to 120), with a second copy of it shifted so the stars don't all dim at once. Needs a `pattern`. It fades the first pattern from its own `opacity`. With a `pattern2` the shifted copy is not drawn and only the first pattern fades. |

### Pattern images

Bonfire ships ten: `blossoms`, `bubbles`, `confetti`, `embers`, `hearts`, `leaves`,
`moons`, `snow`, `stars` and `waves`. An administrator can also upload their own for a
custom theme: PNG or WebP, up to 2 MB, 16 to 1024 pixels a side, not animated. SVG is not
accepted, because it can carry script. An uploaded image is named by a hash and
`.png` or `.webp`, and that name goes in `image` where a built-in name would, for `pattern`
and `pattern2` alike. An app draws it from `/plugins/profiles/themes/pattern-files/<image>`.
It's removed when no theme uses it.

In the editor, each pattern card has an Upload button beside its image list. The image
is then listed as "Your image" and selected, and the preview draws it.

Every decoration also has to:

- **Respect reduced motion.** When the device asks for less motion, anything moving
  stops where it is.
- **Have a low-power version.** `drift` and `twinkle` only move when the administrator
  has turned on richer theme animation, and never on a TV too slow to blur. When blur
  can't be drawn, `frost` is just the fill color. Every theme must still look right that way, so
  the screenshots include it.

Each decoration has a short video recorded from the web client. Build from the
description above and check against the video.

| Decoration | Video | Shows |
| --- | --- | --- |
| `pattern` | [pattern.webm](themes/pattern.webm) | Snowfall, its pattern drifting down. Bloom and Harvest drift the same way, slower, and Starlight's stars twinkle (`twinkle`); there is no video of those yet. |
| `rings` | [rings.webm](themes/rings.webm) | Nightfall on a TV, focus moving along the rings. |
| `frost` | [frost.webm](themes/frost.webm) | Glass, the settings panel opening over the gate. |

## Where each value is used

What every switcher screen is made of. An app that builds these screens from these
values matches the web client.

| Screen part | Uses |
| --- | --- |
| Screen | `background` |
| Logo | `logo.image`, or `logo.text` in `text2`; in the corner `layout.logo` names; 5% in from the edges on a TV |
| "Who's watching?" title | `text1`, `type.title`, `depth.title`; placed by `layout.align` |
| Profiles | `layout.profiles`, `layout.size`, `layout.align` |
| Household heading | `layout.households`; `text3` with `open`, `text1` in a panel |
| Household panel | `surface1`, `border1`, `shape.radius.large`, `depth.sheet` |
| Profile image | `shape.avatar`, `depth.avatar` |
| Profile in focus | `ring` at `shape.ringWidth`, `focus.scale`, `focus.glow`, `nameFocus` |
| Profile name | `name`, `type.name` |
| Add profile tile | `parts.addTile`, `shape.avatar` |
| Primary button | `accent`, `onAccent`, `accentFocus`, `shape.button`, `type.button` |
| Other buttons | `parts.button2`, `shape.button`, `type.button` |
| PIN field | `parts.input`, `shape.input`, `text1`; border `accent` in focus |
| Wrong PIN, and other errors | `danger` |
| Dialogs and forms | `parts.sheet`, `depth.sheet`, `shape.radius.medium` |
| Boxes inside a form | `parts.card` |
| Text fields | `parts.input`, `shape.input`, `text1`; border `accent` in focus |
| Announcement banners | `surface2`, `text1`, `shape.radius.medium`; a stripe on the leading edge in `accent`, `warning` or `danger` by severity |
| Viewing time under a name | `name`, faint; `warning` when the profile can't watch now |

A picker drawn by the server, such as the Moonfin one, draws its own tile images:
`background.fill` and `background.layers` behind a shape in `accent`. A profile with no
image gets a person in its own theme. The `Profiles` and `Switch profile` folders use the
main account's theme. The app draws the names and the tile corners itself.

## Screenshots

[`docs/themes/screens/`](themes/screens/) has every built-in theme on every switcher
screen, at phone, tablet and TV size, taken from the web client. Use them to check your
screens against the web client's. The file is `<theme>/<screen>-<size>.webp`.

- **Screens:** `picker`, `manage`, `pin`, `dialog`, `timeup`, `settings`, `themes`,
  `announcement`, and the logo as an image (`logo`) and as text (`logotext`).
- **Sizes:** `phone` (390×844), `tablet` (820×1180), `tv` (1920×1080, as a TV: nothing
  moves). A theme with `frost` also has `tv-lowpower`, with no blur.
- Everyone in them is made up and the same in every image.

To take them again, run `node tests/screens/shoot.js`. To see which ones a change
moved, run `node tests/screens/diff.js`. Both need Chrome.

## Rules every theme passes

Checked by the validator before a theme is saved, imported or published. A theme that
fails isn't saved, and the settings page names the value at fault.

- **Every required value is present, and every value has the right type.** Nothing that
  isn't in this document is kept.
- **Built-in themes only: text can be read.** A custom theme has no contrast rules and
  gets no contrast warning. "The background" means `background.fill` and every stop of
  every layer, each one checked on its own: a gradient that is fine at one end and
  unreadable at the other fails. See-through colors are mixed onto what is behind them
  first. Each pair is checked separately:

  | Pair | Minimum |
  | --- | --- |
  | `text1`, and the title, on the background | 7 : 1 |
  | `text1` on `panel` | 7 : 1 |
  | `name`, at `type.name.opacity`, on the background | 4.5 : 1 |
  | `text2` on the background | 4.5 : 1 |
  | `text3` on the background | 3 : 1 |
  | `onAccent` on `accent`, and on `accentFocus` | 4.5 : 1 |
  | `button2.text` on `button2.fill` | 4.5 : 1 |
  | `button2.focus.text` on `button2.focus.fill` | 4.5 : 1 |
  | `input` text (`text1`) on `input.fill` | 4.5 : 1 |
  | `ring`, `accent` and `danger` on the background | 3 : 1 |

  The High contrast theme must reach 7 : 1 wherever the table says 4.5.
- **`danger` reads as red and `warning` as amber.**
- **The font is one Bonfire serves**, and it covers every language Bonfire is translated
  into.
- **Every decoration is on the list above.**
- **Nothing reaches outside.** No addresses: fonts and images are files Bonfire holds.

## Getting themes

`GET /plugins/profiles/themes`, no sign-in needed. It's sent with an `ETag`; send it back
as `If-None-Match` and you get `304` until something changes.

```json
{
  "schema": 1,
  "default": "bonfire",
  "profilesCanChoose": true,
  "hidden": [],
  "themes": [ { "schema": 1, "id": "harvest", "revision": 4, "…": "…" } ],
  "fonts": [ { "family": "Lora", "fallback": "serif", "licence": "/plugins/profiles/themes/fonts/lora-OFL.txt",
               "files": [ { "subset": "latin", "url": "/plugins/profiles/themes/fonts/lora-latin.woff2", "format": "woff2",
                            "style": "normal", "weight": [400, 700], "unicodeRange": "U+0000-00FF,…" } ] } ],
  "patterns": [ { "name": "leaves", "url": "/plugins/profiles/themes/patterns/leaves" } ],
  "logo": { "image": "/plugins/profiles/themes/logo/9be1…04.png", "text": null, "alt": "The Hayes House", "corners": "rounded" }
}
```

- **A pattern's image** is the entry in `patterns` whose `name` is the decoration's
  `image`. It's SVG.
- **A font's `files`** are variable fonts covering `weight`, one per `subset`: use the
  one whose `unicodeRange` holds the character. Every one is under the SIL Open Font
  License, at `licence`.
- **A font with no `files`** isn't one Bonfire serves. Use its `fallback`.
- **An uploaded font** has `custom: true` and one file with no `subset`. Use it for
  everything.
- **Custom themes** are the administrator's own, after the built-ins. They pass the same
  rules. Built-ins the administrator hid are left out and listed in `hidden`.

- **Which theme a profile uses** is in `/list`, as each profile's `theme`. When it's
  `null`, use `default`. An id you don't recognize is an alias or a deleted theme: look
  for it in `aliases`, and otherwise use `default`.
- **The logo** is shown in the corner the theme's `layout.logo` names, top right by default. It's an `image`, or
  when there's none a line of `text` in `text2`, or nothing. Give the image `alt` for
  screen readers. `corners` is `rounded` or `square` and covers the image and the backing behind a
  see-through one: draw the same. Keep it inside the TV safe area: 5% in from each edge. Elsewhere it
  sits at least 2rem in from the edge.

## Moonfin theme files

The editor's File menu has **Download for Moonfin**, which writes `<id>.theme.json` for
Moonfin's theme editor. **Load theme file**, and Import on the theme list, also read a
Moonfin file. Moonfin writes colors alpha first (`#AARRGGBB`); Bonfire writes them alpha
last (`#RRGGBBAA`). Both directions convert.

| Bonfire | Moonfin |
| --- | --- |
| `background.fill` | `colors.background`, `book.background`, `book.gradientTop` |
| `colors.panel` | `colors.surface`, `book.gradientBottom` |
| `colors.text1` | `colors.onBackground`, `colors.onSurface`, `book.primaryText` |
| `colors.accent`, `colors.onAccent` | `colors.accent`, `colors.onAccent`, `colors.buttonActive`, `colors.rangeProgress`, `colors.rangeThumb` |
| `parts.button2` `fill`, `text` | `colors.buttonNormal`, `colors.onButtonNormal`, `borders.chipBackground` |
| `parts.button2.focus` `fill`, `text` | `colors.buttonFocused`, `colors.onButtonFocused` |
| `parts.button2.border` | `borders.chipBorder` |
| `colors.ring`, `shape.ringWidth` | `borders.focusBorder`, `colors.inputBorderFocused` |
| `parts.input` `fill`, `line` | `colors.inputBackground`, `colors.inputBorder` |
| `colors.danger` | `colors.error`, `semantic.statusError` |
| `parts.card.line` | `borders.cardBorder` |
| `shape.avatar` | `borders.cardRadius`: a circle is 999, otherwise its radius |
| `shape.button` | `borders.chipRadius`: a pill is 999, a square 0, otherwise its radius |
| `focus.glow` | `borders.focusGlow[0]` |
| `depth.title` | `textGlow[0]`: its own `color` if it has one, otherwise `colors.shadow` at the shadow's opacity. On the way in, the glow's color comes with its alpha |
| `type.font.family` | `fontFamily` |
| a `frost` decoration | `isGlass` |

Worked out from the theme's own colors on the way out: `colors.surfaceVariant`,
`colors.card`, `colors.inputFocused`, `colors.buttonDisabled`, `colors.onButtonDisabled`,
`colors.scrim`, `colors.rangeTrack`, `colors.seekbarBuffered`, and the rest of `book`.
Fixed neutral values, because Bonfire draws none of them: the badge, recording and request
status colors, and the book's placeholder palette (the accent, danger, warning and the
raised surface).

On the way in, the file starts from Bonfire (dark background), the light Jellyfin theme
(light background) or Glass (`isGlass`), and the table above is applied to it. The
background becomes the one color, with no gradient. `text2`, `text3`, the name colors and
`accentFocus` are worked out from `text1` and the accent. A font Bonfire does not serve
keeps the starting theme's font, and the editor says so. A file with a color that is not
`#RRGGBB` or `#AARRGGBB`, or with a `schemaVersion` above 1, is refused whole.

What Moonfin has that Bonfire has no setting for, and is dropped on the way in: the book
and its palette, badge, recording and request-status colors, `navBorder`,
`navColorCycle`, `transparentNavbarSurface`, `isPixel`, the per-corner radii (the top left
is used) and a second or later glow. What Bonfire has that Moonfin does not: background
gradients, images and patterns, the layout, type weights and scale, and the second glow
of a button.

## Changes to this format

`schema` only goes up for a change that would break an app reading version 1, and that
will not happen within Bonfire 8. Adding an optional field or a decoration is not a
break: ignore what you don't know.

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
- [Colours](#colours)
- [Background](#background)
- [Shape](#shape)
- [Type](#type)
- [Depth and focus](#depth-and-focus)
- [Parts](#parts)
- [Layout](#layout)
- [Decorations](#decorations)
- [Where each value is used](#where-each-value-is-used)
- [Rules every theme passes](#rules-every-theme-passes)
- [Getting themes](#getting-themes)
- [Changes to this format](#changes-to-this-format)

## What an app must follow

| Part | Must follow | Notes |
| --- | --- | --- |
| `colors`, `background` | Yes | |
| `shape` | Yes | |
| `parts` | Yes, its colours and shapes | Its type and depth values are optional. |
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
| `id` | string | Lower case letters, digits and `-`. Never changes once published. |
| `revision` | integer | Goes up whenever the file changes. Cache on `id` + `revision`. |
| `scheme` | string | `dark` or `light`: whether the theme's text is light on dark, or dark on light. A light theme gets lighter shadows. Nothing in Bonfire's own CSS may paint white on a theme's surface; `tests/js/lightsafe.js` lists each exception and why. |
| `name` | object | `{ "key": "theme.harvest" }` for a built-in, translated through Bonfire's strings. `{ "text": "Our Theme" }` for an administrator's. |
| `aliases` | string[] | Old ids that now mean this theme. A profile set to one of them gets this one. |

**Colours** are `#rrggbb`, or `#rrggbbaa` when see-through. **Lengths** are pixels at the
size a desktop browser draws them; a TV app scales them the way it scales everything else.

## Colours

| Field | Use |
| --- | --- |
| `accent` | Primary buttons, selected things, focus. |
| `accentFocus` | A primary button under the pointer or in focus. |
| `onAccent` | Text and icons on `accent`. |
| `text1`, `text2`, `text3` | Text: main, secondary, faint. |
| `surface1`, `surface2`, `surface3` | Fills in three steps, usually see-through. |
| `border1`, `border2`, `border3` | Lines in three weights. |
| `panel` | An opaque fill, for things that must hide what scrolls under them. |
| `ring` | The ring around a profile picture in focus. |
| `name`, `nameFocus` | A profile's name under its picture, and in focus. |
| `shadow` | The colour of every shadow. |
| `danger` | Errors and things that delete. |
| `warning` | Warnings. |

Every field is required. `danger` and `warning` can be tuned to suit the theme, but must
still read as red and amber: the validator checks their hue.

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

- `fill` is required. It's the one colour to use when you can't draw gradients.
- `layers` is optional. Layers are drawn in order, first on top, like CSS backgrounds.
- `linear`: `angle` in degrees, 0 pointing up, clockwise.
- `radial`: `shape` is `circle` or `ellipse`, and `x`, `y` are the centre in percent of
  the screen.
- `stops[].at` is a percent along the gradient.

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
  Inter, Nunito, Montserrat, DM Sans, Atkinson Hyperlegible Next, Lora, Playfair Display
  and Quicksand, so you don't need them installed. Each covers every language Bonfire is
  translated into.
- `font.fallback` is `sans-serif`, `serif` or `rounded`. Use it when you can't load the
  family.
- `spacing` is letter spacing in em.
- `scale` multiplies the size the screen already uses.
- `case` is `none` or `upper`.

## Depth and focus

A shadow is `{ "x": 0, "y": 10, "blur": 24, "spread": 0, "opacity": 0.45 }`, in
`colors.shadow` unless it has its own `color`. `null` means none.

```json
"depth": { "avatar": { }, "title": { }, "sheet": null },
"focus": { "scale": 1.05, "glow": { "x": 0, "y": 14, "blur": 30, "color": "#f0a04b59" } }
```

| Field | Use |
| --- | --- |
| `depth.avatar` | Under a profile picture. |
| `depth.title` | Behind the "Who's watching?" title. A text shadow. |
| `depth.sheet` | Under a dialog or form. |
| `focus.scale` | How much a profile grows in focus. `1` is not at all. |
| `focus.glow` | The shadow a profile takes in focus. |

On a TV, focus is what the pointer's hover is on a desktop, so use these for focus.

## Parts

Colours and shapes for the parts every screen is built from. A border is
`{ "width": 1, "style": "solid", "color": "#…" }`, with `style` one of `solid`, `dashed`
or `dotted`. A `line` is just a colour, for a 1-pixel solid edge.

| Part | Fields |
| --- | --- |
| `button2` | The second kind of button: `fill`, `text`, `border`, and the same three under `focus`, plus `focus.glow`. The first kind is `accent` on `onAccent`. |
| `addTile` | The "Add profile" tile: `fill`, `border`, `text`, `focusFill`. |
| `card` | A box inside a form: `fill`, `line`. |
| `sheet` | A dialog or form on its own: `fill`, `border`. Its shadow is `depth.sheet`. |
| `input` | A text field: `fill`, `line`. |

## Layout

```json
"layout": { "households": "panels" }
```

`households` is `panels`, where each household's profiles sit in a box with a heading, or
`open`, where profiles sit straight on the background. With `open`, a screen with one
household shows no heading at all.

## Decorations

Things a theme does that can't be described as colours and shapes. Each has a name from
this list and a few values. Bonfire may add names in later versions, so skip one you
don't know.

| Name | Values | What it draws |
| --- | --- | --- |
| `pattern` | `image`, `size`, `drift` | A picture repeated behind the switcher, faint. `image` is a file from `GET /themes`. `size` is one picture's width and height in pixels. `drift` is `none`, or `down` with `seconds` for one picture's height. |
| `rings` | `gradients`, `gap` | Each profile gets a gradient ring of its own: profile 1 takes the first, profile 2 the second, and so on, starting over at the end. Not the Add tile. `gap` is the colour of the thin space between the ring and the picture. |
| `frost` | `blur` | Dialogs blur what's behind them by `blur` pixels, and the second kind of button by two thirds of that. |

Every decoration also has to:

- **Respect reduced motion.** When the device asks for less motion, anything moving
  stops where it is.
- **Have a low-power version.** On a TV, `drift` doesn't move. When blur can't be
  drawn, `frost` is just the fill colour. Every theme must still look right that way, so
  the screenshots include it.

Each decoration has a short video recorded from the web client, in `docs/themes/`. Build
from the description above and check against the video.

## Where each value is used

What every switcher screen is made of. An app that builds these screens from these
values matches the web client.

| Screen part | Uses |
| --- | --- |
| Screen | `background` |
| "Who's watching?" title | `text1`, `type.title`, `depth.title` |
| Household heading | `layout.households`; `text3` with `open`, `text1` in a panel |
| Household panel | `surface1`, `border1`, `shape.radius.large`, `depth.sheet` |
| Profile picture | `shape.avatar`, `depth.avatar` |
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

A picker drawn by the server, such as the Moonfin one, uses the same values for its tiles:
`background.fill` behind, `shape.avatar`, and `accent` for the selected key.

## Rules every theme passes

Checked by the validator before a theme is saved, imported or published. A theme that
fails isn't saved, and the settings page names the value at fault.

- **Every required value is present, and every value has the right type.** Nothing that
  isn't in this document is kept.
- **Text can be read.** "The background" means `background.fill` and every stop of every
  layer, each one checked on its own: a gradient that is fine at one end and unreadable at
  the other fails. See-through colours are mixed onto what is behind them first. Each pair
  is checked separately:

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
- **Nothing reaches outside.** No addresses: fonts and pictures are files Bonfire holds.

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
  "logo": { "image": "/plugins/profiles/themes/logo/9be1…04.png", "text": null, "alt": "The Hayes House" }
}
```

- **A pattern's picture** is the entry in `patterns` whose `name` is the decoration's
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
  `null`, use `default`. An id you don't recognise is an alias or a deleted theme: look
  for it in `aliases`, and otherwise use `default`.
- **The logo** is shown at the top right of every switcher screen. It's an `image`, or
  when there's none a line of `text` in `text2`, or nothing. Give the image `alt` for
  screen readers. Keep it inside the TV safe area: 5% in from each edge.

## Changes to this format

`schema` only goes up for a change that would break an app reading version 1, and that
will not happen within Bonfire 8. Adding an optional field or a decoration is not a
break: ignore what you don't know.

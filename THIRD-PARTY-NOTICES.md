# Third-party notices

The MIT License in [`LICENSE`](LICENSE) covers the original code of this repository only.
The third-party components listed below are included in the repository and are **not**
covered by that license. Nothing in this repository grants, claims, or implies any license
to these components beyond what their respective rightsholders allow.

## datexport.dll

Path: `src/Patcher/LotroKoniecDev.Infrastructure/datexport.dll`

A proprietary library originally by Turbine, Inc. (rights today associated with Standing
Stone Games / *The Lord of the Rings Online*), circulating in community fan tooling since
approximately 2011. It is included here **solely for interoperability** with the game's
DAT file format — this project does not claim and cannot grant any license to it. It will
be **removed promptly upon request from the rightsholder** (contact: koniecdev@gmail.com).

## Microsoft Visual C++ runtime libraries

Paths: `src/Patcher/LotroKoniecDev.Infrastructure/msvcp71.dll`, `msvcr71.dll`,
`msvcp90.dll`, `msvcr80.dll`

Microsoft Visual C++ redistributable runtime libraries, © Microsoft Corporation. Included
because `datexport.dll` requires them at runtime; distributed under Microsoft's
redistribution terms for the Visual C++ runtime.

## zlib1T.dll

Path: `src/Patcher/LotroKoniecDev.Infrastructure/zlib1T.dll`

A build of the zlib compression library by Jean-loup Gailly and Mark Adler, required by
`datexport.dll` at runtime. zlib is distributed under the zlib license:

> This software is provided 'as-is', without any express or implied warranty. In no event
> will the authors be held liable for any damages arising from the use of this software.
>
> Permission is granted to anyone to use this software for any purpose, including
> commercial applications, and to alter it and redistribute it freely, subject to the
> following restrictions:
>
> 1. The origin of this software must not be misrepresented; you must not claim that you
>    wrote the original software. If you use this software in a product, an acknowledgment
>    in the product documentation would be appreciated but is not required.
> 2. Altered source versions must be plainly marked as such, and must not be
>    misrepresented as being the original software.
> 3. This notice may not be removed or altered from any source distribution.

## Logo and banner artwork

Paths:

- `docs/assets/branding/mofin-logo.png`, `docs/assets/branding/mofin-discord-banner.png` and
  `docs/assets/branding/mofin-discord-strip.png` (the originals, as the artist sent them)
- `src/Frontend/LotroKoniecDev.Frontend/wwwroot/logo.png`, `favicon.ico`,
  `discord-banner.webp` and `discord-strip.webp`
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/wwwroot/logo.png` and `favicon.ico`
- `src/Player/LotroKoniecDev.DesktopApp.Presentation/Assets/app.ico` and `app.png`

The project logo (a quill inside a golden ring with the words "Polskie LOTRO") and the two
Discord banners are © MoFin, all rights reserved. This project uses them with the artist's
permission. They are **not** covered by the MIT License, and forks may not use them: a fork
needs its own logo and artwork. The files above other than the three originals are resized or
re-encoded copies of them. The Discord name and logo shown in the banners are trademarks of
Discord Inc.

## Game content and trademarks

This repository and the lotro-translator.pl platform process text from *The Lord of the
Rings Online* solely to enable the creation of a community Polish translation. No game
content is licensed under the repository license. Standing Stone Games and its marks are
trademarks of Daybreak Game Company LLC. *The Lord of the Rings Online* and the
characters, items, events and places therein are trademarks of Middle-earth Enterprises,
LLC, used under license. This is an unofficial, non-commercial fan project, not
affiliated with or endorsed by Standing Stone Games or Middle-earth Enterprises.

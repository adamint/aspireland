# Third-party notices

The checked-in [Aspireland mark](../web/public/favicon.svg) is the original
sample mark described in [BRAND.md](../BRAND.md). Its colors use the
[Aspire brand palette](https://microsoft.github.io/aspire-brand/); the mark
isn't an official Aspire or transit-authority logo.

- **Poppins** is self-hosted from [`@fontsource/poppins`](https://www.npmjs.com/package/@fontsource/poppins).
  Copyright 2020 The Poppins Project Authors. See the upstream
  [SIL Open Font License 1.1](https://github.com/google/fonts/blob/main/ofl/poppins/OFL.txt).
- **React, React DOM, and Scheduler** are MIT-licensed. Copyright Meta
  Platforms, Inc. and affiliates. See the
  [React license](https://github.com/facebook/react/blob/main/LICENSE).

The pinned license texts are in `web/node_modules` after `npm ci`: look for
`LICENSE` in `@fontsource/poppins`, `react`, `react-dom`, and `scheduler`.
Include their copyright and license texts if you package a copy of the
built web assets. Other dependencies are listed in the
[npm lockfile](../web/package-lock.json), Python lockfiles and .NET project files
and retain their own licenses. These notices don't grant a license to
Aspireland itself.

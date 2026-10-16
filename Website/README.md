# MSC Website (TypeScript + Vite)

Static marketing/docs site for MusicSpeedChanger (macOS). Vanilla TypeScript, no framework.

## Local server (npm)

```zsh
cd Website
npm install
npm run dev      # → http://localhost:5173
```

Other commands:

```zsh
npm run build    # type-check (tsc) + bundle to Website/dist/
npm run preview  # serve the production build → http://localhost:4173
```

## Deploy (GitHub Pages)

Pushing `Website/**` to `devbeta` or `main` triggers
`.github/workflows/pages-website.yml`, which builds `Website/dist/` and deploys it.

One-time repo setup: **Settings → Pages → Source → GitHub Actions**.
Site URL will be `https://embabyty.github.io/MusicSpeedChangerMacOS/`.

`vite.config.ts` uses `base: "./"` so assets work on project pages and locally.

# UnifyEMPI documentation site

The published documentation is built with
[Astro Starlight](https://starlight.astro.build/) from the Markdown and MDX under
`src/content/docs`.

## Local preview

Use Node.js 22.12 or later and pnpm:

```powershell
pnpm install --frozen-lockfile
pnpm dev
```

The production build also validates internal links and Mermaid diagrams:

```powershell
pnpm build
```

GitHub Actions publishes the static output to
<https://lumbridge.github.io/UnifyEMPI/>.

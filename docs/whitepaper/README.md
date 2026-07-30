# Agentwerke Whitepaper

*Governed Agentic Delivery: The Agentwerke Method and Platform Definition* —
the public whitepaper linked from [agentwerke.de](https://agentwerke.de/).

| File | Purpose |
| --- | --- |
| [`agentwerke-whitepaper.md`](agentwerke-whitepaper.md) | Source of truth (Markdown). Edit this first. |
| [`agentwerke-whitepaper.print.html`](agentwerke-whitepaper.print.html) | Print-styled HTML used to typeset the PDF. Keep in sync with the Markdown. |
| [`agentwerke-whitepaper.pdf`](agentwerke-whitepaper.pdf) | Typeset PDF, published at `https://agentwerke.de/agentwerke-whitepaper.pdf` (copied into the `isartor-ai/agentwerke-web` repo root). |

## Regenerating the PDF

After editing the Markdown, mirror the change in
`agentwerke-whitepaper.print.html`, then render with headless Chrome:

```bash
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
  --headless=new --disable-gpu --no-pdf-header-footer \
  --print-to-pdf=agentwerke-whitepaper.pdf agentwerke-whitepaper.print.html
```

Finally copy the PDF to the website repo root (`agentwerke-web/agentwerke-whitepaper.pdf`)
and bump the version/date in both the paper and this folder.

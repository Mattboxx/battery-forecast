# Upstream and pinned revisions

| What | Source | Revision | Checked |
| --- | --- | --- | --- |
| Till Dusk (macOS original) | https://github.com/mattBonfanti/till-dusk | `0e78703264c4d5f8b71ab6cf3425bdf3bb3c973b` ("Till Dusk 1.0") | 2026-10-25 |
| Timestral inference code | https://github.com/mistralai/timestral | `a534bdbd244d64f3d9d743f46c7bf6fc330ed4ff` (same as the original) | 2026-10-25 |
| Timestral-28M weights | https://huggingface.co/mistralai/Timestral-28M | `49b26409ac87b5935539a5d8ffe7f397b720db47` | 2026-10-25 |
| Timestral-5M weights | https://huggingface.co/mistralai/Timestral-5M | `bdd20cfc15a3fea0e4cb5ccef37f8d3ca7aa335f` | 2026-10-25 |
| PyTorch | https://download.pytorch.org/whl/cpu | `2.14.1+cpu` | 2026-10-25 |
| Python | python.org | 3.12.10 x64 | 2026-10-25 |
| .NET SDK | https://dot.net | 10.0.402 | 2026-10-25 |

The weights' `LICENSE` file at the pinned 28M and 5M revisions is byte-identical (ignoring line endings)
to `licenses/Timestral-28M-pesi-MIT-modificata.txt`, the text shipped by the original Till Dusk.

## Relationship with upstream

Battery Forecast is a fork of Till Dusk. The macOS sources (`app/`, `scripts/`) were removed from this
repository to keep it focused on Windows; they remain in the Git history and in the original repository.
Ideas worth porting from upstream can be compared with:

```bash
git fetch upstream
git log --oneline main..upstream/main
```

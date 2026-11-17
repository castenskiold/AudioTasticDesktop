# PDF document sources

These Python scripts generate the project's PDF documents in the dark AudioTastic design, which is based on the logo.

| Script | Output (written to the repository root) |
|---|---|
| `spec.py` + `spec_data.py` | `AudioTastic_Feature_Specification.pdf`: every feature, with what it does, where it is in the UI and what is needed to build it |
| `plan.py` + `plan_data.py` | `AudioTastic_Implementation_Plan.pdf`: build order (phases P0–P14), prerequisites, exit criteria |
| `required.py` | `AudioTastic_Required_Features.pdf`: the list of required features |
| `suggestions.py` | `AudioTastic_Feature_Suggestions.pdf`: the original brainstorm, kept for history only (v1.2) |
| `theme.py` | Shared design: colours, Inter font, cover page, header and footer, tables, feature cards |

## Regenerate

```
pip install "reportlab>=4"
python docs/pdf-src/spec.py
python docs/pdf-src/required.py
python docs/pdf-src/plan.py
```

- **Output folder:** set `AUDIOTASTIC_PDF_OUT` to write the PDFs somewhere other than the repository root.
- **Logo:** the logo is read from `assets/branding/audiotastic-logo-transparent.png`.
- **Fonts:** Inter (SIL Open Font License, see `fonts/LICENSE-Inter.txt`), converted to TrueType so ReportLab can embed it.

## Editing rules

- **Feature content** lives in `spec_data.py`. Each feature is a tuple: `(id, name, what it does, UI location, to build, settings)`.
- **Requirements changes:** update `required.py`, `spec_data.py` and `CLAUDE.md` together.
- **Version numbers:** raise the version in the `T.build(...)` call of each document you change.
- **Progress:** when a phase is finished (or its status changes), update `PROGRESS` in `plan_data.py` (status, date, outcome, still open) and `PREREQ_STATUS` for external prerequisites. The plan shows them in the roadmap, section 3 and on each phase page.

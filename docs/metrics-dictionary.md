# Data dictionary of the run metrics

The Metrics page of TraceableAI offers its figures as three files, for anyone who wants to build their own tables and plots, for example the evaluation section of a paper about the tool. `/metrics/runs.csv` and `/metrics/runs.json` hold one row per finished run, and `/metrics/stages.csv` holds the effort of each run by stage and by model. Every figure is worked out in code from the run's own files when the run finishes (`Runs/RunMetrics.Build.cs` for a systematic review, `Modules/Multivocal/MultivocalRunLog.cs` for a multivocal one), and stored in the metrics store, which outlives the run folders. The export is `Runs/MetricsExport.cs`; a test checks that every column it writes is described here, in the same order.

The files are public, as the page is. They hold no research question (nor its hash), no criteria, no paper text and nothing about who started a run. A run is named by the first eight characters of its id, as on the page, because the whole id opens the run's archive. Multivocal runs are included only while that review is shown on the site. Only finished runs are in the store: a run that failed or was cancelled leaves no row. Rows are ordered by completion time, oldest first.

Read the two kinds of review apart. The `review_type` column says which kind a row is, and the same figure can mean different things in each: a systematic review screens records from scholarly databases and includes studies, while a multivocal review screens grey sources (and, with a formal pool, academic records), keeps those that pass the quality check and cites web pages as they were kept. Pool the figures of one kind only.

## Conventions

Numbers are written with a full stop as the decimal mark and no thousands separator. A share is a fraction from 0 to 1, rounded to four decimals. An empty cell (null in the JSON) means the figure does not apply to the run or was not counted when the run was made; it never means 0. Lines written before 23 November 2026 have no token split, call time, pool figures or kappa basis. Before that date the total `tokens` was also 0 for most runs, because the usage the model provider reported was stored twice encoded and read as nothing. Text cells that start with `=`, `+`, `-` or `@` get a leading `'`, so a spreadsheet does not run them as formulas. Lines end with CRLF, as RFC 4180 asks.

## runs.csv and runs.json

The JSON has the same fields with their types, plus three objects the CSV leaves out: `cost_by_stage` and `cost_by_model` (each with the fields of stages.csv below, in the store's own names) and, for a multivocal run, `sources_by_search` (the new grey sources each search added).

| Column | Unit | What it is and how it is computed |
|---|---|---|
| `run` | text | The first eight characters of the run id. |
| `review_type` | text | `systematic` or `multivocal`. Older lines are read from their settings. |
| `completed_utc` | ISO 8601, UTC | When the run finished (for a multivocal run, when its citation check finished). |
| `tool_version` | SemVer | The TraceableAI version, from the git tag. Empty for a run made before versions came from tags, when every build said 1.0.0. |
| `git_sha` | hex | The full commit the run's build was made from, when the build recorded it. |
| `schema_version` | integer | 2 since versions come from git tags; 1 before. |
| `config_fingerprint` | hex | A hash of the model, the version and the settings below. Rows with the same value were made the same way. |
| `model` | text | The main language model of the run. A second, faster model shows up in `cost_by_model`. |
| `settings` | key=value;… | The settings that shape the output, sorted by name. |
| `identified` | records | Systematic: records the searches found. Multivocal: sources found in both pools. |
| `duplicates_removed` | records | Duplicates removed before screening (multivocal: set aside at screening). |
| `screened` | records | Records or sources screened (included plus excluded). |
| `included` | studies | Systematic: studies that passed both screenings. Multivocal: sources kept for the synthesis (passed the quality check, or appraised with MMAT). |
| `screening_kappa` | κ, −1 to 1 | Cohen's kappa between two independent screenings by the model, over the basis in the next column. For a multivocal run, both pools together. |
| `screening_kappa_basis` | text | `all records` (systematic, every record screened twice); `audit sample` (multivocal from 23 Nov 2026: a seeded random 20% of each pool's first decisions, at least 10; the targeted second screenings are not in it); `all sources` (an older multivocal run, every source screened twice). |
| `uncertain_decisions` | records | Systematic: screening decisions the model was unsure of or the two screenings disagreed on. |
| `full_text_retrieved` | studies | Included studies (kept sources) whose full text was read. |
| `full_text_share` | share | `full_text_retrieved` over `included`. |
| `key_findings` | values | Systematic: key findings extracted (not "not reported"). Multivocal: extracted values that passed the support check. |
| `verified_finding_share` | share | Systematic: findings whose quote was found in the paper. Multivocal: values kept over values judged. |
| `extraction_errors` | studies | Studies or sources whose extraction failed. |
| `values_rejected` | values | Multivocal: values the support check left out, because their quote did not say what the value claims. |
| `mean_quality_points` | points | Multivocal: the mean Table 7 points of the grey sources scored (academic sources are appraised with MMAT and are not in it). |
| `max_quality_points` | points | Multivocal: the most points Table 7 gives. |
| `thematic_synthesis_used` | true/false | Whether the text was written theme by theme (false: it fell back to one section). |
| `themes` | themes | Themes written (0 when the synthesis fell back). |
| `codes` | codes | Systematic: codes in the codebook. |
| `anchored_code_share` | share | Systematic: codes tied to a quote. |
| `studies_coded` | studies | Studies (sources) with at least one code or finding. |
| `studies_uncoded` | studies | Studies (extracted sources) with none. |
| `coding_kappa` | κ | Agreement of the second, independent coding (multivocal: mean over the second placements). |
| `studies_cited` | studies | Included studies or kept sources the text cites. |
| `coverage_share` | share | `studies_cited` over the reference list. |
| `citations_initial` | citations | Citations checked before the repair. |
| `initial_supported` | citations | Of those, first verdict supported. |
| `initial_partly_supported` | citations | First verdict partly supported. |
| `initial_not_supported` | citations | First verdict not supported. |
| `initial_insufficient_evidence` | citations | First verdict insufficient evidence: the abstract alone (systematic) or the kept page (multivocal) was not enough to judge. |
| `initial_unverifiable` | citations | First check could not be done: no text, or the quote was not found in it. |
| `citations` | citations | Citations in the final text, each checked against the cited source. |
| `supported` | citations | Final verdict supported. |
| `partly_supported` | citations | Final verdict partly supported. |
| `not_supported` | citations | Final verdict not supported. |
| `insufficient_evidence` | citations | Final verdict insufficient evidence, as above. |
| `unverifiable` | citations | Final check could not be done, as above. |
| `supported_share` | share | `supported` over `citations`. |
| `second_checked` | citations | Citations checked a second time, independently. |
| `second_check_agreed` | citations | Of those, the ones whose two verdicts agreed. |
| `upgraded_by_second_check` | citations | Citations whose verdict the second check raised. |
| `repaired` | citations | Citations the repair pass rewrote. |
| `removed` | citations | Citations the second repair pass removed as still not supported; not in `citations`. |
| `outside_theme` | citations | Systematic: citations of a study outside the theme's own studies. |
| `invalid_markers_stripped` | markers | Citation markers that named no reference and were removed in code. |
| `synthesis_words` | words | Words of the written results (theme sections and overview). |
| `discussion_words` | words | Words of the discussion. |
| `sentences` | sentences | Sentences of the results and discussion. |
| `cited_sentence_share` | share | Sentences with at least one citation. |
| `citations_per_100_words` | citations | Citation markers per 100 words of results and discussion. |
| `multi_paper_sentence_share` | share | Of the cited sentences, those citing more than one source. |
| `duration_minutes` | minutes | Wall-clock time from the start of the run to its end (multivocal: from the search to the end of the check). |
| `llm_minutes` | minutes | The time of every model call added up, retries and waits included. Calls run side by side, so it can exceed `duration_minutes`. |
| `llm_calls` | calls | Model calls the run made, from `llm-calls.json`. |
| `llm_retries` | calls | Attempts after the first: a call sent again after a busy or failed answer. |
| `llm_failures` | calls | Calls that failed after their last attempt. |
| `calls_without_usage` | calls | Calls with no token usage reported (mostly failed calls); their tokens are not counted. |
| `tokens` | tokens | Input plus output tokens of every call. |
| `input_tokens` | tokens | Prompt tokens, the cached ones included. |
| `cached_input_tokens` | tokens | The part of `input_tokens` the provider served from its prompt cache. |
| `output_tokens` | tokens | Completion tokens, the reasoning tokens included. |
| `reasoning_tokens` | tokens | The part of `output_tokens` the model spent reasoning. |
| `estimated_cost` | money | Empty unless the deployment sets a price table (`Metrics:Prices`). Worked out at download time from `cost_by_model` and today's table, not the price on the day of the run; empty when any model that used tokens has no price. |
| `cost_currency` | ISO 4217 | The currency of `estimated_cost`. |

The multivocal review's two pools follow, first the grey pool (`grey_…`) and then the formal pool of academic records (`formal_…`). They are empty for a systematic run, and the formal columns are empty for a run with grey sources only. Both pools have these columns.

| Column | Unit | What it is and how it is computed |
|---|---|---|
| `grey_identified` | sources | Sources the searches returned (academic: every record, before duplicates). |
| `grey_duplicates_removed` | records | Academic records removed before screening as duplicates by id, DOI or title (0 for grey). |
| `grey_no_address_removed` | records | Academic records removed before screening for having no web address to cite. |
| `grey_duplicates_at_screening` | sources | Sources set aside at screening, the same title under another address. |
| `grey_not_screened` | sources | Sources whose screening failed. |
| `grey_screened` | sources | Included plus excluded. |
| `grey_included` | sources | Sources the screening included. |
| `grey_excluded` | sources | Sources the screening excluded. |
| `grey_kappa` | κ | Cohen's kappa of this pool's two screenings, over the basis in the next column. |
| `grey_kappa_basis` | text | `audit sample` or `all sources`, as for `screening_kappa_basis`. |
| `grey_screened_twice` | sources | Sources screened twice: the targeted ones and the audit sample together. |
| `grey_audit_sample` | sources | Sources in the random audit sample, the only ones κ is computed over. |
| `grey_targeted_second` | sources | Sources screened again because they were included, the decision was unsure, or their text was flagged. |
| `grey_targeted_agreed` | sources | Of those, the ones whose two screenings agreed ("X of Y agreed"); never in κ. |
| `grey_kept` | sources | Grey: at or above the quality threshold. Academic: appraised with MMAT (all are kept). |
| `grey_below_threshold` | sources | Grey sources below the quality threshold. |
| `grey_not_assessed` | sources | Sources with no readable text to assess. |
| `grey_extracted` | sources | Sources extracted against the systematic map. |
| `grey_not_extracted` | sources | Sources whose extraction failed. |
| `grey_abstract_only` | sources | Extracted academic sources read on their abstract alone. |
| `grey_table7_scored` | sources | Grey sources scored on the Table 7 checklist of Garousi et al. (2019). |
| `grey_mean_table7_points` | points | Their mean points. |
| `formal_…` | as above | The same twenty figures for the formal pool, in the same order: `formal_identified` to `formal_abstract_only`. |
| `formal_mmat_appraised` | sources | Academic sources with an MMAT (2018) appraisal. |
| `formal_mmat_empirical` | sources | Of those, the ones appraised as empirical studies, to which MMAT applies. |

## stages.csv

One row per run and stage (`group` = `stage`, named as in `llm-calls.json`, such as `screening` or `citation-check`) and per run and model (`group` = `model`, the model the calls were sent to). Runs made before 23 November 2026 have stage rows with calls, seconds and a total that is often 0, and no model rows.

| Column | Unit | What it is |
|---|---|---|
| `run` | text | As in runs.csv. |
| `review_type` | text | As in runs.csv. |
| `tool_version` | SemVer | As in runs.csv. |
| `group` | text | `stage` or `model`. |
| `name` | text | The stage or the model. |
| `calls` | calls | Model calls. |
| `retries` | calls | Attempts after the first. |
| `failures` | calls | Calls that failed after their last attempt. |
| `calls_without_usage` | calls | Calls with no usage reported. |
| `input_tokens` | tokens | Prompt tokens. |
| `cached_input_tokens` | tokens | The cached part of them. |
| `output_tokens` | tokens | Completion tokens. |
| `reasoning_tokens` | tokens | The reasoning part of them. |
| `tokens` | tokens | Input plus output. |
| `seconds` | seconds | The time of the calls added up. |

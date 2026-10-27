# Project instructions

This is the standalone OpenETRM repository. Run commands with this repository as
the explicit working directory. Do not substitute a similarly named application
or attach its runtime to this preview. Local preview uses this tree's API and an
isolated, unmodified PostgreSQL database.

Read `docs/architecture.md` and `docs/plan.md` before changing the application.
The architecture distinguishes agreed requirements from proposed implementation
choices. Update the decision record when a decision changes.

## Public repository

- Write original implementation and use neutral, explicitly synthetic examples.
- Include only material authorized for public distribution. Preserve dependency
  licenses and required attribution.
- Keep credentials, local configuration, operational logs and generated artifacts
  out of the repository.
- Do not publish conversation transcripts, session exports, internal reasoning or
  local review logs. Public documentation records technical contracts and verified results.
- Do not include customer/company names, competing business-product names or
  comparisons in source, documentation, fixtures or commit messages. Retain the
  exact technical identifiers and attribution required by dependencies/licenses.
- As an exception (D150), the model-provider vendor names Anthropic, OpenAI,
  Google, xAI and Meta and their public API identifiers may appear in source,
  documentation, fixtures and commit messages. Ship no real token prices.
- Public market, exchange and market-operator names are permitted. Use synthetic
  observations; external datasets require verified distribution rights.
- Inspect the complete staged diff, generated files and commit identity before
  publishing. Never publish local settings, secrets or test accounts.
- Preserve copyright and required license notices.
- Keep architecture drafts and implementation changes local until the user
  explicitly approves publication. Do not push unapproved changes.

## Architecture

- TypeScript is presentation only. Keep pricing, schedules, TOU evaluation,
  settlement aggregation, units and other business rules in the managed backend.

- Use TDD for new behavior and bug fixes: write a meaningful failing behavior
  check first, implement the smallest correct change, then refactor with checks
  green. Keep tests independent of the implementation's algorithm.
- Follow DRY and favor simple, elegant code: clear names, small responsibilities
  and explicit contracts. Share proven common code in `src/OpenEtrm.Shared`;
  do not create speculative frameworks or a miscellaneous dumping ground.
- Only check in work the author would be proud to say they wrote. Review the
  result for correctness, readability and restraint; remove temporary scaffolding,
  dead code and needless duplication. Passing tests alone is not that review.

- Add a table, configuration value or dependency only with a named current
  consumer or a concrete executable baseline use case. A seed row or viewer alone
  does not establish use. Keep future extensions in the plan until justified.
- Keep the current units, day-count and compounding implementation small and
  project-owned. Add broader conventions only for a supported consumer with
  independent numerical tests. Persist the selected convention and implementation
  version; no blanket financial-library dependency or imported master catalog.

- NEVER ASSUME user intent. NEVER SILENTLY GO TO DEFAULTS. Financial behavior,
  mark interpretation, dependencies and fallback policies must be explicit in
  the selected versioned contract/context; missing, ambiguous or conflicting
  behavior raises a structured error. UI, imports and agents obey the same rule.
- All-in marks have no inferred upstream curve exposure. Spread marks consume
  only explicitly declared dependencies. Never infer a spread from an outright
  mark, switch pricing modes or allocate exposure from mark levels/correlation.
- Risk results come from deterministic base/up/down PV evaluations under shared
  factor scenarios. Identical pinned inputs, models and numerical policy give
  identical outputs; dependencies identify affected work, not exposure amounts.
- Use an unmodified database and public, reproducible dependencies.
- Retain independent, reusable sides and explicit instrument memberships.
- Apply shocks to immutable market scenarios, never to individual trades.
- Treat pricers as black boxes with declared dependencies and explicit inputs.
- Quantity scaling is a declared capability, separate from market linearity.
- Futures are baseline products; preserve variation-margin state and outstanding
  exposure separately. A forward valuation alone does not implement futures.
- Core reference relationships use exact-version foreign keys, not free-text
  names or arbitrary JSON relationships. Keep public catalogs synthetic and
  placeholders non-executable; real data has separate usage/redistribution rights.
- UI and agent entry points use the same authorized business operations.
- Model/provider-specific types must not enter domain or calculation contracts.
- Numerical policy, units, model versions and snapshot identity are calculation
  inputs. Missing information must not become a successful default result.
- Historical context carries business/effective time and a resolved knowledge
  cutoff through every business read, join, calculation and agent operation.
  Never silently substitute current data or historical permissions.
- Make every write idempotent (D238). Recording a fact that already exists
  (the same business key, value and effective time) is a no-op that returns
  the existing record, never a second row; this covers imports, scrapes,
  collection jobs, closes and retried commands. A retried command carries its
  request id and receives its first outcome. A different value for the same
  business key is a new revision with its own knowledge time, never an
  overwrite. Reads resolve revisions by knowledge time under an explicit
  policy, never by row or insertion order.

## Verification and review

- Use isolated disposable resources and synthetic data. Do not operate on
  existing applications or production when testing.
- Run relevant correctness and integration checks before publishing changes.
- Obtain independent adversarial review for changes involving authorization,
  temporal behavior, async execution, numerical correctness, scenario reuse or
  release/deployment boundaries. Resolve findings before publication.
- Benchmarks must identify workload, unique contracts, scenarios, evaluated work,
  memory and environment. Do not infer performance from trade count alone.
- Document actual capability and remaining work; a design is not evidence that a
  feature is implemented or qualified for production.

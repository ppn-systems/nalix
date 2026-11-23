#!/usr/bin/env bash
# Runs the already-built tests of one solution and retries the failures once.
#
# Usage: run-tests.sh <solution> <results-dir> <coverage: true|false>
#
# A test that fails and then passes on the retry is listed in the job summary and does not fail the job, so
# flaky tests stay visible. Only the failed tests are retried: rerunning the whole suite doubled the job.
set -uo pipefail

solution="$1"
results="$2"
coverage="$3"
python=$(command -v python3 || command -v python)

args=("$solution" --configuration Release --no-build --no-restore --logger trx --blame-hang --blame-hang-timeout 120000 -m)

first=("${args[@]}" --results-directory "$results")
# Microsoft.CodeCoverage, not coverlet: coverlet's instrumentation made Nalix.Codec.Tests (crypto loops) take
# ~4 min instead of ~30 s.
if [ "$coverage" = "true" ]; then
  first+=(--settings tests/coverage.runsettings --collect "Code Coverage")
fi

exit_code=0
dotnet test "${first[@]}" || exit_code=$?
if [ "$exit_code" -eq 0 ]; then
  # "Nothing failed" is not "it ran": a solution whose test targets are not found exits 0 without a result file.
  if ! find "$results" -name '*.trx' -print -quit | grep -q .; then
    echo "::error::dotnet test passed without writing a .trx file: no test ran."
    exit 1
  fi
  exit 0
fi

filter=$("$python" .github/scripts/failed-tests-filter.py "$results")
if [ -z "$filter" ]; then
  echo "::error::dotnet test failed (exit code $exit_code) without a failed test in the results (build or crash)."
  exit "$exit_code"
fi

retry=("${args[@]}" --results-directory "$results/retry")
if [ "$filter" = "ALL" ]; then
  echo "Too many failures to filter: retrying the whole suite."
else
  echo "Retrying the failed tests only: $filter"
  retry+=(--filter "$filter")
fi

retry_code=0
dotnet test "${retry[@]}" || retry_code=$?
if [ "$retry_code" -ne 0 ]; then
  echo "Tests failed again on the retry."
  exit "$retry_code"
fi

{
  echo "### Flaky tests (failed, then passed on the retry)"
  echo
  echo "$filter" | tr '|' '\n' | sed 's/^FullyQualifiedName=/- `/; s/$/`/'
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
echo "::warning::Some tests failed and then passed on the retry; see the job summary."

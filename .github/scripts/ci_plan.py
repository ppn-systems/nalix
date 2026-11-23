#!/usr/bin/env python3
"""Plans a run of .github/workflows/ci.yml: which shards must run, and what each one builds.

A shard is one CI job: a group of test projects, the full build, or the workflow lint. Its key is a hash of the
git blob id of every tracked file that can change its result:

  - the directories of its projects and of everything they reference (ProjectReference, transitively);
  - the shared build files: every tracked file outside a project directory that is not listed in NOT_INPUT;
  - the source generator project, which the Directory.Build.props files add to every project.

A shard that passes saves an Actions cache entry named after its key. A later run skips the shard when such an
entry exists and was saved from the default branch or from the run's own ref, so a pull request can never be
skipped on the word of another pull request. Any doubt (API error, no entry) runs the shard.

Usage:
  ci_plan.py plan     Print the plan; in CI, also write the job outputs and the step summary.
  ci_plan.py inputs NAME   Print the files hashed for one shard (for debugging a key).

Environment (CI): GITHUB_REPOSITORY, GITHUB_REF, GH_TOKEN, GITHUB_OUTPUT, GITHUB_STEP_SUMMARY,
CI_FORCE=true (run everything), CI_TRUST_OWN_REF=false (pull request from a fork).
"""

import fnmatch
import hashlib
import json
import os
import posixpath
import re
import subprocess
import sys

# Bump to invalidate every pass marker (for example after a runner image or SDK change broke something).
SALT = "1"
DEFAULT_REF = "refs/heads/master"

# kind test: `projects` are test project directories. They are built for net10.0 without analyzers and run.
# kind build: compiles src/Nalix.sln and extensions/*.sln for every target framework with analyzers on.
# kind lint: actionlint over the workflows. It has no projects and does not depend on the shared build files.
# Patterns match project directories; `*` also matches `/`.
SHARDS = [
    {"name": "base", "kind": "test", "os": "ubuntu-latest", "coverage": True, "projects": [
        "tests/Nalix.Abstractions.Tests", "tests/Nalix.Environment.Tests", "tests/Nalix.Framework.Tests",
        "tests/Nalix.Codec.Tests", "tests/Nalix.Runtime.Tests"]},
    {"name": "network", "kind": "test", "os": "ubuntu-latest", "coverage": True, "projects": [
        "tests/Nalix.Network.Tests"]},
    {"name": "hosting", "kind": "test", "os": "ubuntu-latest", "coverage": True, "projects": [
        "tests/Nalix.Hosting.Tests", "tests/Nalix.Integration.Tests"]},
    {"name": "sdk", "kind": "test", "os": "ubuntu-latest", "coverage": True, "projects": [
        "tests/Nalix.SDK.Tests"]},
    {"name": "analyzers", "kind": "test", "os": "ubuntu-latest", "coverage": False, "projects": [
        "tests/Nalix.Analyzers.Tests"]},
    {"name": "extensions", "kind": "test", "os": "ubuntu-latest", "coverage": False, "projects": [
        "extensions/tests/*"]},
    # The client-side libraries (Nalix.SDK and what it ships with) also run on Windows.
    {"name": "windows-client", "kind": "test", "os": "windows-latest", "coverage": False, "projects": [
        "tests/Nalix.Abstractions.Tests", "tests/Nalix.Environment.Tests", "tests/Nalix.Codec.Tests",
        "tests/Nalix.SDK.Tests"]},
    {"name": "build", "kind": "build", "projects": ["src/*", "extensions/*", "analyzers/*"]},
    {"name": "lint", "kind": "lint", "projects": [], "inputs": [".github/workflows/*", ".github/scripts/*"]},
]

# Tracked files that cannot change a build or test result. `*` also matches `/`.
NOT_INPUT = ("*.md", "LICENSE", ".gitignore", ".claude/*", ".github/*", "assets/*", "benchmarks/*", "samples/*",
             "context7.json", "pack.cmd", "pack.ps1", "push-nuget.ps1")
# ...except these, which define the CI run itself.
CI_FILES = (".github/workflows/ci.yml", ".github/scripts/*")
# Added to every project by src/, tests/ and extensions/tests/ Directory.Build.props, so no csproj names it.
GENERATORS = "analyzers/Nalix.Analyzers.Generators"

PROJECT_REFERENCE = re.compile(r'<ProjectReference\s[^>]*?Include="([^"]+)"')


def matches(path, patterns):
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)


def tracked_files():
    """Map every tracked path to its git blob id."""
    out = subprocess.run(["git", "ls-files", "-s", "-z"], check=True, capture_output=True).stdout.decode("utf-8")
    files = {}
    for entry in filter(None, out.split("\0")):
        meta, path = entry.split("\t", 1)
        files[path] = meta.split()[1]
    return files


class Repo:
    def __init__(self):
        self.files = tracked_files()
        # project directory -> csproj path
        self.projects = {posixpath.dirname(p): p for p in self.files if p.endswith(".csproj")}
        self._references = {}

    def references(self, directory):
        """Directories of the projects one project references directly."""
        if directory not in self._references:
            with open(self.projects[directory], encoding="utf-8-sig") as handle:
                text = handle.read()
            found = set()
            for include in PROJECT_REFERENCE.findall(text):
                include = include.replace("\\", "/")
                for prefix in ("$(MSBuildThisFileDirectory)", "$(MSBuildProjectDirectory)/"):
                    include = include.replace(prefix, "")
                target = posixpath.dirname(posixpath.normpath(posixpath.join(directory, include)))
                if "$(" in include or target not in self.projects:
                    sys.exit(f"{self.projects[directory]}: cannot resolve ProjectReference '{include}'")
                found.add(target)
            self._references[directory] = found
        return self._references[directory]

    def closure(self, directories):
        seen, todo = set(), list(directories)
        while todo:
            directory = todo.pop()
            if directory not in seen:
                seen.add(directory)
                todo.extend(self.references(directory))
        return seen

    def expand(self, patterns):
        """Project directories that match the shard's patterns, in a stable order."""
        found = []
        for pattern in patterns:
            hits = sorted(d for d in self.projects if fnmatch.fnmatchcase(d, pattern))
            if not hits:
                sys.exit(f"shard pattern '{pattern}' matches no project")
            found.extend(d for d in hits if d not in found)
        return found

    def inputs(self, shard):
        """Tracked files that can change the shard's result."""
        if shard["kind"] == "lint":
            return sorted(p for p in self.files if matches(p, shard["inputs"]))
        roots = [d + "/" for d in self.closure(self.expand(shard["projects"]) + [GENERATORS])]
        every_project = tuple(d + "/" for d in self.projects)
        selected = []
        for path in self.files:
            if matches(path, NOT_INPUT) and not matches(path, CI_FILES):
                continue
            if path.startswith(every_project):
                if any(path.startswith(root) for root in roots):
                    selected.append(path)
            else:
                selected.append(path)  # shared build file
        return sorted(selected)

    def key(self, shard):
        digest = hashlib.sha256()
        digest.update(json.dumps([SALT, shard], sort_keys=True).encode("utf-8"))
        for path in self.inputs(shard):
            digest.update(f"{path}\0{self.files[path]}\n".encode("utf-8"))
        return f"ci-pass-{shard['name']}-{digest.hexdigest()[:32]}"

    def check(self):
        """Fail when a test project would run in no Linux shard, or is missing from its solution."""
        covered = set()
        for shard in SHARDS:
            if shard["kind"] == "test" and shard["os"] == "ubuntu-latest":
                covered.update(self.expand(shard["projects"]))
        tests = {d for d in self.projects if d.endswith(".Tests") and not matches(d, NOT_INPUT)}
        missing = sorted(tests - covered)
        if missing:
            sys.exit(f"test projects in no Linux shard of ci_plan.py: {', '.join(missing)}")
        with open("tests/Nalix.Tests.sln", encoding="utf-8-sig") as handle:
            solution = handle.read()
        absent = sorted(d for d in tests if d.startswith("tests/") and posixpath.basename(d) + ".csproj" not in solution)
        if absent:
            sys.exit(f"test projects missing from tests/Nalix.Tests.sln: {', '.join(absent)}")


def solution(repo, shard):
    """A .slnx with only the shard's test projects: what they reference is built through ProjectReference,
    for the test target framework only."""
    lines = [f'  <Project Path="{repo.projects[d]}" />' for d in repo.expand(shard["projects"])]
    return "<Solution>\n" + "\n".join(lines) + "\n</Solution>\n"


def passed_before(key):
    """True when a pass marker for this key was saved from a ref this run may trust."""
    trusted = {DEFAULT_REF}
    if os.environ.get("CI_TRUST_OWN_REF", "true") == "true" and os.environ.get("GITHUB_REF"):
        trusted.add(os.environ["GITHUB_REF"])
    try:
        out = subprocess.run(
            ["gh", "api", "-X", "GET", f"repos/{os.environ['GITHUB_REPOSITORY']}/actions/caches",
             "-f", f"key={key}", "-f", "per_page=100"],
            check=True, capture_output=True, timeout=30).stdout
        caches = json.loads(out)["actions_caches"]
    except Exception as error:  # fail open: an unanswered question runs the shard
        print(f"cache lookup failed for {key}: {error}", file=sys.stderr)
        return False
    return any(cache["key"] == key and cache["ref"] in trusted for cache in caches)


def plan(repo):
    repo.check()
    force = os.environ.get("CI_FORCE") == "true"
    online = "GITHUB_REPOSITORY" in os.environ
    rows, tests, outputs = [], [], {}
    for shard in SHARDS:
        key = repo.key(shard)
        if force:
            run, why = True, "forced"
        elif online and passed_before(key):
            run, why = False, "same inputs already passed"
        else:
            run, why = True, "inputs changed" if online else "offline"
        rows.append((shard["name"], "run" if run else "skip", why, key))
        if shard["kind"] == "test":
            if run:
                tests.append({"name": shard["name"], "os": shard["os"], "coverage": shard["coverage"],
                              "key": key, "solution": solution(repo, shard)})
        else:
            outputs[shard["name"]] = "true" if run else "false"
            outputs[shard["name"] + "_key"] = key
    outputs["tests"] = json.dumps(tests, separators=(",", ":"))

    for name, action, why, key in rows:
        print(f"{name:16} {action:5} {why:28} {key}")
    if "GITHUB_OUTPUT" in os.environ:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as handle:
            for name, value in outputs.items():
                handle.write(f"{name}={value}\n")
    if "GITHUB_STEP_SUMMARY" in os.environ:
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as handle:
            handle.write("### CI plan\n\n| Shard | Action | Reason |\n| :--- | :--- | :--- |\n")
            for name, action, why, _ in rows:
                handle.write(f"| {name} | {action} | {why} |\n")


def main():
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    repo = Repo()
    if command == "plan":
        plan(repo)
    elif command == "inputs" and len(sys.argv) == 3:
        shard = next((s for s in SHARDS if s["name"] == sys.argv[2]), None)
        if shard is None:
            sys.exit(f"unknown shard '{sys.argv[2]}'")
        print("\n".join(repo.inputs(shard)))
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()

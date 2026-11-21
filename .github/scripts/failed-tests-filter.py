"""Prints a `dotnet test --filter` expression that selects the tests that failed in the given .trx files.

Usage: python3 failed-tests-filter.py <results-dir> [max-tests]

Prints nothing when no test failed, and "ALL" when more than max-tests (default 50) failed or a failed
result cannot be mapped to a test method: rerunning only the failures is then not worth it.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def main() -> int:
    results_dir = sys.argv[1]
    max_tests = int(sys.argv[2]) if len(sys.argv) > 2 else 50
    failed = set()

    for path in glob.glob(os.path.join(results_dir, "**", "*.trx"), recursive=True):
        root = ET.parse(path).getroot()
        methods = {}
        for test in root.iter(NS + "UnitTest"):
            method = test.find(NS + "TestMethod")
            if method is not None:
                methods[test.get("id")] = f'{method.get("className")}.{method.get("name")}'

        for result in root.iter(NS + "UnitTestResult"):
            if result.get("outcome") in ("Failed", "Error", "Timeout", "Aborted"):
                name = methods.get(result.get("testId"))
                if name is None:
                    print("ALL")
                    return 0
                failed.add(name)

    if len(failed) > max_tests:
        print("ALL")
    elif failed:
        print("|".join(f"FullyQualifiedName={name}" for name in sorted(failed)))
    return 0


if __name__ == "__main__":
    sys.exit(main())

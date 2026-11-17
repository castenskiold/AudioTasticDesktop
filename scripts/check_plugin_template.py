#!/usr/bin/env python3
"""P4 exit check (PG-02): a plugin built only from the AudioTastic.Sdk NuGet package loads, runs and unloads.

1. Packs the SDK into artifacts/packages.
2. Installs the 'dotnet new' template from templates/AudioTastic.Plugin into a private template hive (your own
   dotnet new templates are not touched) and creates a plugin project in a temporary folder outside the repository.
3. Builds it against artifacts/packages (plus nuget.org for the SDK's own dependencies), with a private NuGet cache
   so a rebuilt SDK package of the same version is never taken from a stale cache.
4. Runs the host test 'Plugin_built_from_the_sdk_package_loads_runs_and_unloads' on the built plugin (that test is
   compiled only with -p:TemplateCheck=true, so the normal test run has nothing to skip).

Usage: python scripts/check_plugin_template.py [--keep]
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
PACKAGES = REPO / "artifacts" / "packages"


def run(args, **kwargs):
    print("> " + " ".join(str(a) for a in args), flush=True)
    subprocess.run([str(a) for a in args], check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--keep", action="store_true", help="keep the temporary folder")
    args = parser.parse_args()

    run(["dotnet", "pack", REPO / "src" / "AudioTastic.Sdk", "-c", "Release", "-o", PACKAGES, "--nologo"])

    work = Path(tempfile.mkdtemp(prefix="audiotastic-template-"))
    try:
        hive = work / "hive"
        env = dict(os.environ, NUGET_PACKAGES=str(work / "nuget"))
        run(["dotnet", "new", "install", REPO / "templates" / "AudioTastic.Plugin", "--debug:custom-hive", hive])
        project = work / "HelloTemplate"
        run(["dotnet", "new", "audiotastic-plugin", "-n", "HelloTemplate", "-o", project, "--debug:custom-hive", hive])
        (project / "nuget.config").write_text(f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="{PACKAGES}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
""", encoding="utf-8")
        run(["dotnet", "build", project, "-c", "Release", "--nologo"], env=env)
        output = project / "bin" / "Release" / "net10.0"
        package = project / "bin" / "Release" / "HelloTemplate.atplugin"
        if not package.exists():
            sys.exit(f"The template build did not produce {package}")

        test_env = dict(os.environ, AUDIOTASTIC_TEMPLATE_PLUGIN=str(output))
        run(["dotnet", "test", REPO / "tests" / "AudioTastic.Plugins.Host.Tests", "-p:TemplateCheck=true", "--",
             "--filter-method", "*Plugin_built_from_the_sdk_package_loads_runs_and_unloads"], env=test_env)
        print("OK: the template plugin built from the SDK package loads, runs and unloads.")
    finally:
        if args.keep:
            print(f"Kept {work}")
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()

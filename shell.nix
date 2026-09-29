let
  pkgs = import <nixpkgs> {};
  roslynLanguageServer = pkgs.writeShellScriptBin "roslyn-language-server" ''
    exec ${pkgs.roslyn-ls}/bin/Microsoft.CodeAnalysis.LanguageServer "$@"
  '';
in
pkgs.mkShell {
  packages = [
    pkgs.dotnet-sdk_10
    pkgs.python3
    pkgs.python3Packages.pip
    pkgs.roslyn-ls
    roslynLanguageServer
    pkgs.direnv
    pkgs.git
  ];

  shellHook = ''
    export DOTNET_ROOT="${pkgs.dotnet-sdk_10}/share/dotnet"

    if ! command -v jb >/dev/null 2>&1; then
      echo "jb (JetBrains ReSharper command-line tools) is not on PATH."
      echo "This repo's inspectcode gate needs it. Install it yourself, for example:"
      echo "  dotnet tool install -g JetBrains.ReSharper.GlobalTools --version 2026.1.3"
      echo "Then make sure ~/.dotnet/tools is on PATH."
    fi
  '';
}

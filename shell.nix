let
  pkgs = import <nixpkgs> {};
  roslynLanguageServer = pkgs.writeShellScriptBin "roslyn-language-server" ''
    exec ${pkgs.roslyn-ls}/bin/Microsoft.CodeAnalysis.LanguageServer "$@"
  '';
in
pkgs.mkShell {
  packages = [
    pkgs.dotnet-sdk_10
    pkgs.nodejs_22
    pkgs.python3
    pkgs.python3Packages.pip
    pkgs.roslyn-ls
    roslynLanguageServer
    pkgs.direnv
    pkgs.git
  ];

  shellHook = ''
    export DOTNET_ROOT="${pkgs.dotnet-sdk_10}/share/dotnet"
    export DOTNET_CLI_HOME="$PWD/.direnv/dotnet-home"
    export NUGET_PACKAGES="$PWD/.direnv/nuget"
    export DOTNET_TOOLS_DIR="$PWD/.direnv/dotnet-tools"
    export NPM_CONFIG_PREFIX="$PWD/.direnv/npm-global"
    export NPM_CONFIG_CACHE="$PWD/.direnv/npm-cache"
    export PATH="$DOTNET_TOOLS_DIR:$NPM_CONFIG_PREFIX/bin:$PATH"

    mkdir -p "$DOTNET_CLI_HOME" "$DOTNET_TOOLS_DIR" "$NPM_CONFIG_PREFIX" "$NPM_CONFIG_CACHE"

    if [ ! -x "$DOTNET_TOOLS_DIR/jb" ]; then
      dotnet tool install \
        --tool-path "$DOTNET_TOOLS_DIR" \
        JetBrains.ReSharper.GlobalTools \
        --version 2026.1.3
    fi

    if [ ! -x "$NPM_CONFIG_PREFIX/bin/trellis" ]; then
      npm install --global --no-fund --no-audit @mindfoldhq/trellis@0.6.17
    fi
  '';
}

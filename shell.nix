# nix-shell --command "bash update_and_start.sh"
# export NIXPKGS_ALLOW_INSECURE=1 && nix-shell --impure --command "bash update_and_start.sh"
{ pkgs ? import <nixpkgs> { } }:
with pkgs;
mkShell {
  packages = [ icu dotnetCorePackages.sdk_10_0-bin dotnet-ef ];
}

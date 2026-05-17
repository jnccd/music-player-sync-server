{
  description = "Nix Shell Wrapper";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.11";
    numtide-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, ... }@inputs:
    inputs.numtide-utils.lib.eachDefaultSystem (system:
      let pkgs = import nixpkgs { inherit system; };
      in {
        devShells = rec {
          service = import ./shell.nix { inherit pkgs; };

          default = service;
        };
      });
}

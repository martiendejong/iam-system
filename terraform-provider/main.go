package main

import (
	"context"
	"flag"
	"log"

	"github.com/hashicorp/terraform-plugin-framework/providerserver"
	"terraform-provider-iam/internal/provider"
)

// Run "go generate" to format example terraform files and generate the docs for the registry/website.

// If you do not have terraform installed, you can remove the line below, but it would be best
// to ensure the googled documentation is formatted properly.
//go:generate terraform fmt -recursive ./examples/

// Run the docs generation tool.
//go:generate go run github.com/hashicorp/terraform-plugin-docs/cmd/tfplugindocs

var (
	// These will be set by the goreleaser configuration to appropriate values
	// for the compiled binary.
	version string = "dev"
)

func main() {
	var debug bool

	flag.BoolVar(&debug, "debug", false, "set to true to run the provider with support for debuggers like delve")
	flag.Parse()

	opts := providerserver.ServeOpts{
		Address: "registry.terraform.io/iam-system/iam",
		Debug:   debug,
	}

	err := providerserver.Serve(context.Background(), provider.New(version), opts)
	if err != nil {
		log.Fatal(err.Error())
	}
}

package provider

import (
	"context"
	"os"

	"github.com/hashicorp/terraform-plugin-framework/datasource"
	"github.com/hashicorp/terraform-plugin-framework/path"
	"github.com/hashicorp/terraform-plugin-framework/provider"
	"github.com/hashicorp/terraform-plugin-framework/provider/schema"
	"github.com/hashicorp/terraform-plugin-framework/resource"
	"github.com/hashicorp/terraform-plugin-framework/types"
	"github.com/hashicorp/terraform-plugin-log/tflog"
)

// Ensure the implementation satisfies the expected interfaces.
var (
	_ provider.Provider = &iamProvider{}
)

// iamProvider is the provider implementation.
type iamProvider struct {
	// version is set to the provider version on release, "dev" when the
	// provider is built and run locally, and "test" when running acceptance
	// testing.
	version string
}

// iamProviderModel maps provider schema data to a Go type.
type iamProviderModel struct {
	BaseURL  types.String `tfsdk:"base_url"`
	ApiKey   types.String `tfsdk:"api_key"`
	Username types.String `tfsdk:"username"`
	Password types.String `tfsdk:"password"`
}

// New is a helper function to simplify provider server and testing implementation.
func New(version string) func() provider.Provider {
	return func() provider.Provider {
		return &iamProvider{
			version: version,
		}
	}
}

// Metadata returns the provider type name.
func (p *iamProvider) Metadata(_ context.Context, _ provider.MetadataRequest, resp *provider.MetadataResponse) {
	resp.TypeName = "iam"
	resp.Version = p.version
}

// Schema defines the provider-level schema for configuration data.
func (p *iamProvider) Schema(_ context.Context, _ provider.SchemaRequest, resp *provider.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "The IAM provider allows you to manage Identity and Access Management resources " +
			"including tenants, users, roles, devices, and policies. It supports hierarchical " +
			"multi-tenant organizations with IoT device management and spatial policy inheritance.",
		Attributes: map[string]schema.Attribute{
			"base_url": schema.StringAttribute{
				Description: "The base URL of the IAM System API (e.g., https://iam.example.com). " +
					"Can also be set via the IAM_BASE_URL environment variable.",
				Optional: true,
			},
			"api_key": schema.StringAttribute{
				Description: "API key for authenticating with the IAM System API. " +
					"Can also be set via the IAM_API_KEY environment variable. " +
					"Mutually exclusive with username/password authentication.",
				Optional:  true,
				Sensitive: true,
			},
			"username": schema.StringAttribute{
				Description: "Username (email) for authenticating with the IAM System API. " +
					"Can also be set via the IAM_USERNAME environment variable. " +
					"Must be used together with password.",
				Optional: true,
			},
			"password": schema.StringAttribute{
				Description: "Password for authenticating with the IAM System API. " +
					"Can also be set via the IAM_PASSWORD environment variable. " +
					"Must be used together with username.",
				Optional:  true,
				Sensitive: true,
			},
		},
	}
}

// Configure prepares an IAM API client for data sources and resources.
func (p *iamProvider) Configure(ctx context.Context, req provider.ConfigureRequest, resp *provider.ConfigureResponse) {
	tflog.Info(ctx, "Configuring IAM client")

	// Retrieve provider data from configuration.
	var config iamProviderModel
	diags := req.Config.Get(ctx, &config)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	// If practitioner provided a configuration value for any of the attributes,
	// it must be a known value.
	if config.BaseURL.IsUnknown() {
		resp.Diagnostics.AddAttributeError(
			path.Root("base_url"),
			"Unknown IAM API Base URL",
			"The provider cannot create the IAM API client as there is an unknown configuration value "+
				"for the IAM API base URL. Either target apply the source of the value first, set the value "+
				"statically in the configuration, or use the IAM_BASE_URL environment variable.",
		)
	}

	if config.ApiKey.IsUnknown() {
		resp.Diagnostics.AddAttributeError(
			path.Root("api_key"),
			"Unknown IAM API Key",
			"The provider cannot create the IAM API client as there is an unknown configuration value "+
				"for the IAM API key. Either target apply the source of the value first, set the value "+
				"statically in the configuration, or use the IAM_API_KEY environment variable.",
		)
	}

	if resp.Diagnostics.HasError() {
		return
	}

	// Default values from environment variables.
	baseURL := os.Getenv("IAM_BASE_URL")
	apiKey := os.Getenv("IAM_API_KEY")
	username := os.Getenv("IAM_USERNAME")
	password := os.Getenv("IAM_PASSWORD")

	// Override with configuration values if set.
	if !config.BaseURL.IsNull() {
		baseURL = config.BaseURL.ValueString()
	}
	if !config.ApiKey.IsNull() {
		apiKey = config.ApiKey.ValueString()
	}
	if !config.Username.IsNull() {
		username = config.Username.ValueString()
	}
	if !config.Password.IsNull() {
		password = config.Password.ValueString()
	}

	// Validate required configuration.
	if baseURL == "" {
		resp.Diagnostics.AddAttributeError(
			path.Root("base_url"),
			"Missing IAM API Base URL",
			"The provider cannot create the IAM API client as there is a missing or empty value for "+
				"the IAM API base URL. Set the base_url value in the configuration or use the "+
				"IAM_BASE_URL environment variable. If either is already set, ensure the value is not empty.",
		)
	}

	if apiKey == "" && (username == "" || password == "") {
		resp.Diagnostics.AddError(
			"Missing IAM API Credentials",
			"The provider cannot create the IAM API client as there are no valid credentials. "+
				"Provide either an api_key or both username and password in the configuration, "+
				"or set the IAM_API_KEY, IAM_USERNAME, and IAM_PASSWORD environment variables.",
		)
	}

	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Creating IAM client", map[string]interface{}{
		"iam_base_url": baseURL,
	})

	// Create the IAM API client.
	client, err := NewClient(baseURL, apiKey, username, password)
	if err != nil {
		resp.Diagnostics.AddError(
			"Unable to Create IAM API Client",
			"An unexpected error occurred when creating the IAM API client. "+
				"If the error is not clear, please contact the provider developers.\n\n"+
				"IAM Client Error: "+err.Error(),
		)
		return
	}

	// Make the client available during DataSource and Resource type Configure methods.
	resp.DataSourceData = client
	resp.ResourceData = client

	tflog.Info(ctx, "Configured IAM client", map[string]interface{}{
		"iam_base_url": baseURL,
	})
}

// Resources defines the resources implemented in the provider.
func (p *iamProvider) Resources(_ context.Context) []func() resource.Resource {
	return []func() resource.Resource{
		NewTenantResource,
		NewUserResource,
		NewRoleResource,
		NewDeviceResource,
		NewPolicyResource,
	}
}

// DataSources defines the data sources implemented in the provider.
func (p *iamProvider) DataSources(_ context.Context) []func() datasource.DataSource {
	return []func() datasource.DataSource{
		NewTenantDataSource,
		NewUserDataSource,
		NewDeviceDataSource,
	}
}

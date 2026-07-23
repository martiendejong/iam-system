package provider

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/hashicorp/terraform-plugin-framework/datasource"
	"github.com/hashicorp/terraform-plugin-framework/datasource/schema"
	"github.com/hashicorp/terraform-plugin-framework/types"
	"github.com/hashicorp/terraform-plugin-log/tflog"
)

// Ensure the implementation satisfies the expected interfaces.
var (
	_ datasource.DataSource              = &userDataSource{}
	_ datasource.DataSourceWithConfigure = &userDataSource{}
)

// NewUserDataSource is a helper function to simplify the provider implementation.
func NewUserDataSource() datasource.DataSource {
	return &userDataSource{}
}

// userDataSource is the data source implementation.
type userDataSource struct {
	client *IamClient
}

// userDataSourceModel maps the data source schema data.
type userDataSourceModel struct {
	ID               types.String `tfsdk:"id"`
	Email            types.String `tfsdk:"email"`
	FirstName        types.String `tfsdk:"first_name"`
	LastName         types.String `tfsdk:"last_name"`
	EmailConfirmed   types.Bool   `tfsdk:"email_confirmed"`
	TwoFactorEnabled types.Bool   `tfsdk:"two_factor_enabled"`
	IsActive         types.Bool   `tfsdk:"is_active"`
	CreatedAt        types.String `tfsdk:"created_at"`
	LastLoginAt      types.String `tfsdk:"last_login_at"`
}

// Metadata returns the data source type name.
func (d *userDataSource) Metadata(_ context.Context, req datasource.MetadataRequest, resp *datasource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_user"
}

// Schema defines the schema for the data source.
func (d *userDataSource) Schema(_ context.Context, _ datasource.SchemaRequest, resp *datasource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Use this data source to get information about an existing user in the IAM system.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Description: "The unique identifier of the user (UUID).",
				Required:    true,
			},
			"email": schema.StringAttribute{
				Description: "The email address of the user.",
				Computed:    true,
			},
			"first_name": schema.StringAttribute{
				Description: "The first name of the user.",
				Computed:    true,
			},
			"last_name": schema.StringAttribute{
				Description: "The last name of the user.",
				Computed:    true,
			},
			"email_confirmed": schema.BoolAttribute{
				Description: "Whether the user's email has been confirmed.",
				Computed:    true,
			},
			"two_factor_enabled": schema.BoolAttribute{
				Description: "Whether two-factor authentication is enabled.",
				Computed:    true,
			},
			"is_active": schema.BoolAttribute{
				Description: "Whether the user is active.",
				Computed:    true,
			},
			"created_at": schema.StringAttribute{
				Description: "Timestamp when the user was created.",
				Computed:    true,
			},
			"last_login_at": schema.StringAttribute{
				Description: "Timestamp of the user's last login.",
				Computed:    true,
			},
		},
	}
}

// Configure adds the provider configured client to the data source.
func (d *userDataSource) Configure(_ context.Context, req datasource.ConfigureRequest, resp *datasource.ConfigureResponse) {
	if req.ProviderData == nil {
		return
	}

	client, ok := req.ProviderData.(*IamClient)
	if !ok {
		resp.Diagnostics.AddError(
			"Unexpected Data Source Configure Type",
			fmt.Sprintf("Expected *IamClient, got: %T. Please report this issue to the provider developers.", req.ProviderData),
		)
		return
	}

	d.client = client
}

// Read refreshes the Terraform state with the latest data.
func (d *userDataSource) Read(ctx context.Context, req datasource.ReadRequest, resp *datasource.ReadResponse) {
	var config userDataSourceModel
	diags := req.Config.Get(ctx, &config)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	userID := config.ID.ValueString()

	tflog.Debug(ctx, "Reading user data source", map[string]interface{}{
		"id": userID,
	})

	body, _, err := d.client.doRequestWithStatus("GET", fmt.Sprintf("/api/users/%s", userID), nil)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Reading User",
			"Could not read user ID "+userID+": "+err.Error(),
		)
		return
	}

	var user userAPIResponse
	if err := json.Unmarshal(body, &user); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse user read response: "+err.Error(),
		)
		return
	}

	// Map response to model.
	config.ID = types.StringValue(user.ID)
	config.Email = types.StringValue(user.Email)
	config.FirstName = types.StringValue(user.FirstName)
	config.LastName = types.StringValue(user.LastName)
	config.EmailConfirmed = types.BoolValue(user.EmailConfirmed)
	config.TwoFactorEnabled = types.BoolValue(user.TwoFactorEnabled)
	config.IsActive = types.BoolValue(user.IsActive)
	config.CreatedAt = types.StringValue(user.CreatedAt)

	if user.LastLoginAt != nil {
		config.LastLoginAt = types.StringValue(*user.LastLoginAt)
	} else {
		config.LastLoginAt = types.StringNull()
	}

	diags = resp.State.Set(ctx, &config)
	resp.Diagnostics.Append(diags...)
}

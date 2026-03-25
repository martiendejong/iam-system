package provider

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/hashicorp/terraform-plugin-framework/diag"
	"github.com/hashicorp/terraform-plugin-framework/path"
	"github.com/hashicorp/terraform-plugin-framework/resource"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/booldefault"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/planmodifier"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringplanmodifier"
	"github.com/hashicorp/terraform-plugin-framework/types"
	"github.com/hashicorp/terraform-plugin-log/tflog"
)

// Ensure the implementation satisfies the expected interfaces.
var (
	_ resource.Resource                = &userResource{}
	_ resource.ResourceWithConfigure   = &userResource{}
	_ resource.ResourceWithImportState = &userResource{}
)

// NewUserResource is a helper function to simplify the provider implementation.
func NewUserResource() resource.Resource {
	return &userResource{}
}

// userResource is the resource implementation.
type userResource struct {
	client *IamClient
}

// userResourceModel maps the resource schema data.
type userResourceModel struct {
	ID               types.String `tfsdk:"id"`
	Email            types.String `tfsdk:"email"`
	Password         types.String `tfsdk:"password"`
	FirstName        types.String `tfsdk:"first_name"`
	LastName         types.String `tfsdk:"last_name"`
	EmailConfirmed   types.Bool   `tfsdk:"email_confirmed"`
	TwoFactorEnabled types.Bool   `tfsdk:"two_factor_enabled"`
	IsActive         types.Bool   `tfsdk:"is_active"`
	Roles            types.List   `tfsdk:"roles"`
	CreatedAt        types.String `tfsdk:"created_at"`
	LastLoginAt      types.String `tfsdk:"last_login_at"`
}

// userRoleModel maps role assignment data.
type userRoleModel struct {
	RoleID   types.String `tfsdk:"role_id"`
	TenantID types.String `tfsdk:"tenant_id"`
}

// userAPIRegisterResponse represents the registration response from the API.
type userAPIRegisterResponse struct {
	Message string `json:"message"`
	UserID  string `json:"userId"`
}

// userAPIResponse represents the JSON response from the API for user reads.
type userAPIResponse struct {
	ID               string         `json:"id"`
	Email            string         `json:"email"`
	FirstName        string         `json:"firstName"`
	LastName         string         `json:"lastName"`
	EmailConfirmed   bool           `json:"emailConfirmed"`
	TwoFactorEnabled bool           `json:"twoFactorEnabled"`
	IsActive         bool           `json:"isActive"`
	CreatedAt        string         `json:"createdAt"`
	LastLoginAt      *string        `json:"lastLoginAt"`
	Roles            []userRoleResp `json:"roles"`
}

// userRoleResp represents a role in the API response.
type userRoleResp struct {
	ID        string  `json:"id"`
	Name      string  `json:"name"`
	TenantID  *string `json:"tenantId"`
	ExpiresAt *string `json:"expiresAt"`
}

// Metadata returns the resource type name.
func (r *userResource) Metadata(_ context.Context, req resource.MetadataRequest, resp *resource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_user"
}

// Schema defines the schema for the resource.
func (r *userResource) Schema(_ context.Context, _ resource.SchemaRequest, resp *resource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Manages a user in the IAM system. Users are created via registration and can be " +
			"assigned roles within tenants.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Description: "The unique identifier of the user (UUID).",
				Computed:    true,
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.UseStateForUnknown(),
				},
			},
			"email": schema.StringAttribute{
				Description: "The email address of the user. Used as the login identifier.",
				Required:    true,
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.RequiresReplace(),
				},
			},
			"password": schema.StringAttribute{
				Description: "The password for the user. Only used during creation. " +
					"Changes to this field will trigger a recreation of the user.",
				Required:  true,
				Sensitive: true,
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.RequiresReplace(),
				},
			},
			"first_name": schema.StringAttribute{
				Description: "The first name of the user.",
				Required:    true,
			},
			"last_name": schema.StringAttribute{
				Description: "The last name of the user.",
				Required:    true,
			},
			"email_confirmed": schema.BoolAttribute{
				Description: "Whether the user's email has been confirmed.",
				Computed:    true,
			},
			"two_factor_enabled": schema.BoolAttribute{
				Description: "Whether two-factor authentication is enabled for the user.",
				Computed:    true,
			},
			"is_active": schema.BoolAttribute{
				Description: "Whether the user is active. Set to false to deactivate.",
				Optional:    true,
				Computed:    true,
				Default:     booldefault.StaticBool(true),
			},
			"roles": schema.ListNestedAttribute{
				Description: "List of role assignments for the user.",
				Optional:    true,
				NestedObject: schema.NestedAttributeObject{
					Attributes: map[string]schema.Attribute{
						"role_id": schema.StringAttribute{
							Description: "The ID of the role to assign.",
							Required:    true,
						},
						"tenant_id": schema.StringAttribute{
							Description: "The tenant ID to scope the role assignment. " +
								"Null for global role assignments.",
							Optional: true,
						},
					},
				},
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

// Configure adds the provider configured client to the resource.
func (r *userResource) Configure(_ context.Context, req resource.ConfigureRequest, resp *resource.ConfigureResponse) {
	if req.ProviderData == nil {
		return
	}

	client, ok := req.ProviderData.(*IamClient)
	if !ok {
		resp.Diagnostics.AddError(
			"Unexpected Resource Configure Type",
			fmt.Sprintf("Expected *IamClient, got: %T. Please report this issue to the provider developers.", req.ProviderData),
		)
		return
	}

	r.client = client
}

// Create creates the resource and sets the initial Terraform state.
func (r *userResource) Create(ctx context.Context, req resource.CreateRequest, resp *resource.CreateResponse) {
	var plan userResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Creating user", map[string]interface{}{
		"email": plan.Email.ValueString(),
	})

	// Register the user.
	registerBody := map[string]string{
		"email":     plan.Email.ValueString(),
		"password":  plan.Password.ValueString(),
		"firstName": plan.FirstName.ValueString(),
		"lastName":  plan.LastName.ValueString(),
	}

	body, err := r.client.doRequest("POST", "/api/auth/register", registerBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Creating User",
			"Could not register user, unexpected error: "+err.Error(),
		)
		return
	}

	var registerResp userAPIRegisterResponse
	if err := json.Unmarshal(body, &registerResp); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse user registration response: "+err.Error(),
		)
		return
	}

	plan.ID = types.StringValue(registerResp.UserID)

	// Handle role assignments.
	if !plan.Roles.IsNull() && !plan.Roles.IsUnknown() {
		var roles []userRoleModel
		diags = plan.Roles.ElementsAs(ctx, &roles, false)
		resp.Diagnostics.Append(diags...)
		if resp.Diagnostics.HasError() {
			return
		}

		for _, role := range roles {
			roleAssignBody := map[string]interface{}{
				"roleId": role.RoleID.ValueString(),
			}
			if !role.TenantID.IsNull() && !role.TenantID.IsUnknown() {
				roleAssignBody["tenantId"] = role.TenantID.ValueString()
			}

			_, err := r.client.doRequest("POST", fmt.Sprintf("/api/users/%s/roles", registerResp.UserID), roleAssignBody)
			if err != nil {
				resp.Diagnostics.AddWarning(
					"Error Assigning Role",
					fmt.Sprintf("Could not assign role %s to user: %s", role.RoleID.ValueString(), err.Error()),
				)
			}
		}
	}

	// Handle activation state.
	if !plan.IsActive.IsNull() && !plan.IsActive.IsUnknown() && !plan.IsActive.ValueBool() {
		_, err := r.client.doRequest("POST", fmt.Sprintf("/api/users/%s/deactivate", registerResp.UserID), nil)
		if err != nil {
			resp.Diagnostics.AddWarning(
				"Error Deactivating User",
				"Could not deactivate user after creation: "+err.Error(),
			)
		}
	}

	// Read back the complete state.
	r.readUserIntoModel(ctx, registerResp.UserID, &plan, &resp.Diagnostics)
	if resp.Diagnostics.HasError() {
		return
	}

	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Created user", map[string]interface{}{
		"id":    registerResp.UserID,
		"email": plan.Email.ValueString(),
	})
}

// Read refreshes the Terraform state with the latest data.
func (r *userResource) Read(ctx context.Context, req resource.ReadRequest, resp *resource.ReadResponse) {
	var state userResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Reading user", map[string]interface{}{
		"id": state.ID.ValueString(),
	})

	body, statusCode, err := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/users/%s", state.ID.ValueString()), nil)
	if err != nil {
		if statusCode == 404 {
			tflog.Warn(ctx, "User not found, removing from state", map[string]interface{}{
				"id": state.ID.ValueString(),
			})
			resp.State.RemoveResource(ctx)
			return
		}
		resp.Diagnostics.AddError(
			"Error Reading User",
			"Could not read user ID "+state.ID.ValueString()+": "+err.Error(),
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
	state.ID = types.StringValue(user.ID)
	state.Email = types.StringValue(user.Email)
	state.FirstName = types.StringValue(user.FirstName)
	state.LastName = types.StringValue(user.LastName)
	state.EmailConfirmed = types.BoolValue(user.EmailConfirmed)
	state.TwoFactorEnabled = types.BoolValue(user.TwoFactorEnabled)
	state.IsActive = types.BoolValue(user.IsActive)
	state.CreatedAt = types.StringValue(user.CreatedAt)

	if user.LastLoginAt != nil {
		state.LastLoginAt = types.StringValue(*user.LastLoginAt)
	} else {
		state.LastLoginAt = types.StringNull()
	}

	// Password is write-only; preserve current state value.

	diags = resp.State.Set(ctx, &state)
	resp.Diagnostics.Append(diags...)
}

// Update updates the resource and sets the updated Terraform state on success.
func (r *userResource) Update(ctx context.Context, req resource.UpdateRequest, resp *resource.UpdateResponse) {
	var plan userResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	var state userResourceModel
	diags = req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	userID := state.ID.ValueString()

	tflog.Debug(ctx, "Updating user", map[string]interface{}{
		"id": userID,
	})

	// Update profile fields (first_name, last_name) -- uses the admin user endpoint.
	// The IAM API currently supports updating first/last name via PUT /api/users/me.
	// For Terraform, we use a direct approach assuming the provider has admin access.
	updateBody := map[string]string{}
	if plan.FirstName.ValueString() != state.FirstName.ValueString() {
		updateBody["firstName"] = plan.FirstName.ValueString()
	}
	if plan.LastName.ValueString() != state.LastName.ValueString() {
		updateBody["lastName"] = plan.LastName.ValueString()
	}

	if len(updateBody) > 0 {
		// Note: The IAM API may need a dedicated admin endpoint for updating other users' profiles.
		// This uses the assumption of a PUT /api/users/{id} endpoint or similar admin capability.
		_, err := r.client.doRequest("PUT", fmt.Sprintf("/api/users/%s", userID), updateBody)
		if err != nil {
			resp.Diagnostics.AddWarning(
				"Error Updating User Profile",
				"Could not update user profile: "+err.Error()+". Profile updates may require the /api/users/me endpoint.",
			)
		}
	}

	// Handle activation state changes.
	planActive := plan.IsActive.ValueBool()
	stateActive := state.IsActive.ValueBool()

	if planActive != stateActive {
		if planActive {
			_, err := r.client.doRequest("POST", fmt.Sprintf("/api/users/%s/activate", userID), nil)
			if err != nil {
				resp.Diagnostics.AddError(
					"Error Activating User",
					"Could not activate user: "+err.Error(),
				)
				return
			}
		} else {
			_, err := r.client.doRequest("POST", fmt.Sprintf("/api/users/%s/deactivate", userID), nil)
			if err != nil {
				resp.Diagnostics.AddError(
					"Error Deactivating User",
					"Could not deactivate user: "+err.Error(),
				)
				return
			}
		}
	}

	// Handle role changes.
	if !plan.Roles.IsNull() && !plan.Roles.IsUnknown() {
		var planRoles []userRoleModel
		diags = plan.Roles.ElementsAs(ctx, &planRoles, false)
		resp.Diagnostics.Append(diags...)

		var stateRoles []userRoleModel
		if !state.Roles.IsNull() && !state.Roles.IsUnknown() {
			diags = state.Roles.ElementsAs(ctx, &stateRoles, false)
			resp.Diagnostics.Append(diags...)
		}

		if resp.Diagnostics.HasError() {
			return
		}

		// Build maps of existing and desired roles for diff.
		existingRoles := make(map[string]bool)
		for _, r := range stateRoles {
			existingRoles[r.RoleID.ValueString()] = true
		}

		desiredRoles := make(map[string]bool)
		for _, r := range planRoles {
			desiredRoles[r.RoleID.ValueString()] = true
		}

		// Add new roles.
		for _, role := range planRoles {
			if !existingRoles[role.RoleID.ValueString()] {
				roleAssignBody := map[string]interface{}{
					"roleId": role.RoleID.ValueString(),
				}
				if !role.TenantID.IsNull() && !role.TenantID.IsUnknown() {
					roleAssignBody["tenantId"] = role.TenantID.ValueString()
				}
				_, err := r.client.doRequest("POST", fmt.Sprintf("/api/users/%s/roles", userID), roleAssignBody)
				if err != nil {
					resp.Diagnostics.AddWarning(
						"Error Assigning Role",
						fmt.Sprintf("Could not assign role %s: %s", role.RoleID.ValueString(), err.Error()),
					)
				}
			}
		}

		// Remove roles that are no longer desired.
		for _, role := range stateRoles {
			if !desiredRoles[role.RoleID.ValueString()] {
				_, err := r.client.doRequest("DELETE", fmt.Sprintf("/api/users/%s/roles/%s", userID, role.RoleID.ValueString()), nil)
				if err != nil {
					resp.Diagnostics.AddWarning(
						"Error Removing Role",
						fmt.Sprintf("Could not remove role %s: %s", role.RoleID.ValueString(), err.Error()),
					)
				}
			}
		}
	}

	// Read back the complete state.
	r.readUserIntoModel(ctx, userID, &plan, &resp.Diagnostics)
	if resp.Diagnostics.HasError() {
		return
	}

	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Updated user", map[string]interface{}{
		"id": userID,
	})
}

// Delete deletes the resource and removes the Terraform state on success.
func (r *userResource) Delete(ctx context.Context, req resource.DeleteRequest, resp *resource.DeleteResponse) {
	var state userResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	userID := state.ID.ValueString()

	tflog.Debug(ctx, "Deleting (deactivating) user", map[string]interface{}{
		"id": userID,
	})

	// The IAM API does not have a hard delete for users. We deactivate instead.
	_, statusCode, err := r.client.doRequestWithStatus("POST", fmt.Sprintf("/api/users/%s/deactivate", userID), nil)
	if err != nil {
		if statusCode == 404 {
			tflog.Warn(ctx, "User already deleted/not found", map[string]interface{}{
				"id": userID,
			})
			return
		}
		resp.Diagnostics.AddError(
			"Error Deactivating User",
			"Could not deactivate user ID "+userID+": "+err.Error(),
		)
		return
	}

	// Also remove all role assignments.
	if !state.Roles.IsNull() && !state.Roles.IsUnknown() {
		var roles []userRoleModel
		diags = state.Roles.ElementsAs(ctx, &roles, false)
		resp.Diagnostics.Append(diags...)

		for _, role := range roles {
			_, _ = r.client.doRequest("DELETE", fmt.Sprintf("/api/users/%s/roles/%s", userID, role.RoleID.ValueString()), nil)
		}
	}

	tflog.Info(ctx, "Deactivated user", map[string]interface{}{
		"id": userID,
	})
}

// ImportState imports an existing resource by its ID.
func (r *userResource) ImportState(ctx context.Context, req resource.ImportStateRequest, resp *resource.ImportStateResponse) {
	resource.ImportStatePassthroughID(ctx, path.Root("id"), req, resp)
}

// readUserIntoModel reads a user from the API and populates the model.
func (r *userResource) readUserIntoModel(ctx context.Context, userID string, model *userResourceModel, diagnostics *diag.Diagnostics) {
	body, _, err := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/users/%s", userID), nil)
	if err != nil {
		diagnostics.AddError(
			"Error Reading User",
			"Could not read user ID "+userID+": "+err.Error(),
		)
		return
	}

	var user userAPIResponse
	if err := json.Unmarshal(body, &user); err != nil {
		diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse user read response: "+err.Error(),
		)
		return
	}

	model.ID = types.StringValue(user.ID)
	model.Email = types.StringValue(user.Email)
	model.FirstName = types.StringValue(user.FirstName)
	model.LastName = types.StringValue(user.LastName)
	model.EmailConfirmed = types.BoolValue(user.EmailConfirmed)
	model.TwoFactorEnabled = types.BoolValue(user.TwoFactorEnabled)
	model.IsActive = types.BoolValue(user.IsActive)
	model.CreatedAt = types.StringValue(user.CreatedAt)

	if user.LastLoginAt != nil {
		model.LastLoginAt = types.StringValue(*user.LastLoginAt)
	} else {
		model.LastLoginAt = types.StringNull()
	}
}

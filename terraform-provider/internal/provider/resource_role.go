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
	_ resource.Resource                = &roleResource{}
	_ resource.ResourceWithConfigure   = &roleResource{}
	_ resource.ResourceWithImportState = &roleResource{}
)

// NewRoleResource is a helper function to simplify the provider implementation.
func NewRoleResource() resource.Resource {
	return &roleResource{}
}

// roleResource is the resource implementation.
type roleResource struct {
	client *IamClient
}

// roleResourceModel maps the resource schema data.
type roleResourceModel struct {
	ID           types.String `tfsdk:"id"`
	Name         types.String `tfsdk:"name"`
	Description  types.String `tfsdk:"description"`
	TenantID     types.String `tfsdk:"tenant_id"`
	Permissions  types.List   `tfsdk:"permissions"`
	IsSystemRole types.Bool   `tfsdk:"is_system_role"`
	CreatedAt    types.String `tfsdk:"created_at"`
	UpdatedAt    types.String `tfsdk:"updated_at"`
}

// roleAPIResponse represents the JSON response from the API.
type roleAPIResponse struct {
	ID           string   `json:"id"`
	Name         string   `json:"name"`
	Description  string   `json:"description"`
	TenantID     *string  `json:"tenantId"`
	Permissions  []string `json:"permissions"`
	IsSystemRole bool     `json:"isSystemRole"`
	CreatedAt    string   `json:"createdAt"`
	UpdatedAt    string   `json:"updatedAt"`
	UserCount    int      `json:"userCount"`
}

// Metadata returns the resource type name.
func (r *roleResource) Metadata(_ context.Context, req resource.MetadataRequest, resp *resource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_role"
}

// Schema defines the schema for the resource.
func (r *roleResource) Schema(_ context.Context, _ resource.SchemaRequest, resp *resource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Manages a role in the IAM system. Roles define sets of permissions and can be " +
			"scoped to a specific tenant or be global. System roles cannot be modified or deleted.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Description: "The unique identifier of the role (UUID).",
				Computed:    true,
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.UseStateForUnknown(),
				},
			},
			"name": schema.StringAttribute{
				Description: "The display name of the role. Must be unique within the tenant scope.",
				Required:    true,
			},
			"description": schema.StringAttribute{
				Description: "A description of the role and its intended purpose.",
				Optional:    true,
				Computed:    true,
			},
			"tenant_id": schema.StringAttribute{
				Description: "The tenant this role belongs to. Null for global roles.",
				Optional:    true,
			},
			"permissions": schema.ListAttribute{
				Description: "List of permission strings assigned to this role " +
					"(e.g., ['Building.View', 'Building.Manage', 'HVAC.Control']).",
				Optional:    true,
				ElementType: types.StringType,
			},
			"is_system_role": schema.BoolAttribute{
				Description: "Whether this is a system role. System roles cannot be modified or deleted. " +
					"Custom roles created via Terraform are never system roles.",
				Computed: true,
				Default:  booldefault.StaticBool(false),
			},
			"created_at": schema.StringAttribute{
				Description: "Timestamp when the role was created.",
				Computed:    true,
			},
			"updated_at": schema.StringAttribute{
				Description: "Timestamp when the role was last updated.",
				Computed:    true,
			},
		},
	}
}

// Configure adds the provider configured client to the resource.
func (r *roleResource) Configure(_ context.Context, req resource.ConfigureRequest, resp *resource.ConfigureResponse) {
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
func (r *roleResource) Create(ctx context.Context, req resource.CreateRequest, resp *resource.CreateResponse) {
	var plan roleResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Creating role", map[string]interface{}{
		"name": plan.Name.ValueString(),
	})

	// Build request body.
	requestBody := map[string]interface{}{
		"name": plan.Name.ValueString(),
	}

	if !plan.Description.IsNull() && !plan.Description.IsUnknown() {
		requestBody["description"] = plan.Description.ValueString()
	}

	if !plan.TenantID.IsNull() && !plan.TenantID.IsUnknown() {
		requestBody["tenantId"] = plan.TenantID.ValueString()
	}

	if !plan.Permissions.IsNull() && !plan.Permissions.IsUnknown() {
		var permissions []string
		diags = plan.Permissions.ElementsAs(ctx, &permissions, false)
		resp.Diagnostics.Append(diags...)
		if resp.Diagnostics.HasError() {
			return
		}
		requestBody["permissions"] = permissions
	}

	// Call the API.
	body, err := r.client.doRequest("POST", "/api/roles", requestBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Creating Role",
			"Could not create role, unexpected error: "+err.Error(),
		)
		return
	}

	// Parse the response.
	var role roleAPIResponse
	if err := json.Unmarshal(body, &role); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse role creation response: "+err.Error(),
		)
		return
	}

	// Map response to model.
	r.mapRoleResponseToModel(ctx, &role, &plan, &resp.Diagnostics)
	if resp.Diagnostics.HasError() {
		return
	}

	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Created role", map[string]interface{}{
		"id":   role.ID,
		"name": role.Name,
	})
}

// Read refreshes the Terraform state with the latest data.
func (r *roleResource) Read(ctx context.Context, req resource.ReadRequest, resp *resource.ReadResponse) {
	var state roleResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Reading role", map[string]interface{}{
		"id": state.ID.ValueString(),
	})

	body, statusCode, err := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/roles/%s", state.ID.ValueString()), nil)
	if err != nil {
		if statusCode == 404 {
			tflog.Warn(ctx, "Role not found, removing from state", map[string]interface{}{
				"id": state.ID.ValueString(),
			})
			resp.State.RemoveResource(ctx)
			return
		}
		resp.Diagnostics.AddError(
			"Error Reading Role",
			"Could not read role ID "+state.ID.ValueString()+": "+err.Error(),
		)
		return
	}

	var role roleAPIResponse
	if err := json.Unmarshal(body, &role); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse role read response: "+err.Error(),
		)
		return
	}

	r.mapRoleResponseToModel(ctx, &role, &state, &resp.Diagnostics)
	if resp.Diagnostics.HasError() {
		return
	}

	diags = resp.State.Set(ctx, &state)
	resp.Diagnostics.Append(diags...)
}

// Update updates the resource and sets the updated Terraform state on success.
func (r *roleResource) Update(ctx context.Context, req resource.UpdateRequest, resp *resource.UpdateResponse) {
	var plan roleResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	var state roleResourceModel
	diags = req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	roleID := state.ID.ValueString()

	tflog.Debug(ctx, "Updating role", map[string]interface{}{
		"id": roleID,
	})

	// Build the update request body.
	requestBody := map[string]interface{}{}

	if plan.Name.ValueString() != state.Name.ValueString() {
		requestBody["name"] = plan.Name.ValueString()
	}

	if !plan.Description.IsNull() && !plan.Description.IsUnknown() {
		requestBody["description"] = plan.Description.ValueString()
	}

	if !plan.Permissions.IsNull() && !plan.Permissions.IsUnknown() {
		var permissions []string
		diags = plan.Permissions.ElementsAs(ctx, &permissions, false)
		resp.Diagnostics.Append(diags...)
		if resp.Diagnostics.HasError() {
			return
		}
		requestBody["permissions"] = permissions
	}

	// Call the API.
	body, err := r.client.doRequest("PUT", fmt.Sprintf("/api/roles/%s", roleID), requestBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Updating Role",
			"Could not update role ID "+roleID+": "+err.Error(),
		)
		return
	}

	// Parse the response.
	var role roleAPIResponse
	if err := json.Unmarshal(body, &role); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse role update response: "+err.Error(),
		)
		return
	}

	// Read back the full role to get all fields.
	readBody, _, readErr := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/roles/%s", roleID), nil)
	if readErr == nil {
		var fullRole roleAPIResponse
		if json.Unmarshal(readBody, &fullRole) == nil {
			r.mapRoleResponseToModel(ctx, &fullRole, &plan, &resp.Diagnostics)
		}
	} else {
		r.mapRoleResponseToModel(ctx, &role, &plan, &resp.Diagnostics)
	}

	if resp.Diagnostics.HasError() {
		return
	}

	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Updated role", map[string]interface{}{
		"id": roleID,
	})
}

// Delete deletes the resource and removes the Terraform state on success.
func (r *roleResource) Delete(ctx context.Context, req resource.DeleteRequest, resp *resource.DeleteResponse) {
	var state roleResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	roleID := state.ID.ValueString()

	tflog.Debug(ctx, "Deleting role", map[string]interface{}{
		"id": roleID,
	})

	_, statusCode, err := r.client.doRequestWithStatus("DELETE", fmt.Sprintf("/api/roles/%s", roleID), nil)
	if err != nil {
		if statusCode == 404 {
			tflog.Warn(ctx, "Role already deleted/not found", map[string]interface{}{
				"id": roleID,
			})
			return
		}
		resp.Diagnostics.AddError(
			"Error Deleting Role",
			"Could not delete role ID "+roleID+": "+err.Error(),
		)
		return
	}

	tflog.Info(ctx, "Deleted role", map[string]interface{}{
		"id": roleID,
	})
}

// ImportState imports an existing resource by its ID.
func (r *roleResource) ImportState(ctx context.Context, req resource.ImportStateRequest, resp *resource.ImportStateResponse) {
	resource.ImportStatePassthroughID(ctx, path.Root("id"), req, resp)
}

// mapRoleResponseToModel maps an API response to the Terraform model.
func (r *roleResource) mapRoleResponseToModel(ctx context.Context, role *roleAPIResponse, model *roleResourceModel, diagnostics *diag.Diagnostics) {
	model.ID = types.StringValue(role.ID)
	model.Name = types.StringValue(role.Name)
	model.Description = types.StringValue(role.Description)
	model.IsSystemRole = types.BoolValue(role.IsSystemRole)
	model.CreatedAt = types.StringValue(role.CreatedAt)
	model.UpdatedAt = types.StringValue(role.UpdatedAt)

	if role.TenantID != nil {
		model.TenantID = types.StringValue(*role.TenantID)
	} else {
		model.TenantID = types.StringNull()
	}

	if role.Permissions != nil && len(role.Permissions) > 0 {
		permList, diags := types.ListValueFrom(ctx, types.StringType, role.Permissions)
		diagnostics.Append(diags...)
		model.Permissions = permList
	} else {
		model.Permissions = types.ListNull(types.StringType)
	}
}

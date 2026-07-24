import { api } from './api';

export interface TenantBranding {
  id?: string;
  tenantId: string;
  logoUrl?: string | null;
  primaryColor?: string | null;
  secondaryColor?: string | null;
  backgroundUrl?: string | null;
  customCss?: string | null;
  emailHeaderHtml?: string | null;
  emailFooterHtml?: string | null;
  faviconUrl?: string | null;
  loginTitle?: string | null;
  loginSubtitle?: string | null;
  whiteLabelEnabled: boolean;
  customDomain?: string | null;
  createdAt?: string;
  updatedAt?: string;
}

export interface PublicTenantBranding {
  tenantSlug?: string | null;
  tenantName?: string | null;
  logoUrl?: string | null;
  primaryColor: string;
  secondaryColor: string;
  backgroundUrl?: string | null;
  customCss?: string | null;
  faviconUrl?: string | null;
  loginTitle?: string | null;
  loginSubtitle?: string | null;
  whiteLabelEnabled: boolean;
}

export interface UpsertBrandingRequest {
  logoUrl?: string | null;
  primaryColor?: string | null;
  secondaryColor?: string | null;
  backgroundUrl?: string | null;
  customCss?: string | null;
  emailHeaderHtml?: string | null;
  emailFooterHtml?: string | null;
  faviconUrl?: string | null;
  loginTitle?: string | null;
  loginSubtitle?: string | null;
  whiteLabelEnabled: boolean;
  customDomain?: string | null;
}

const client = api.getClient();

export const brandingApi = {
  async getBranding(tenantId: string): Promise<TenantBranding> {
    const response = await client.get<TenantBranding>(`/branding/${tenantId}`);
    return response.data;
  },

  async upsertBranding(tenantId: string, data: UpsertBrandingRequest): Promise<TenantBranding> {
    const response = await client.put<TenantBranding>(`/branding/${tenantId}`, data);
    return response.data;
  },

  async deleteBranding(tenantId: string): Promise<void> {
    await client.delete(`/branding/${tenantId}`);
  },

  async getPublicBranding(tenantSlug: string): Promise<PublicTenantBranding> {
    const response = await client.get<PublicTenantBranding>(`/branding/public/${tenantSlug}`);
    return response.data;
  },

  async getBrandingByDomain(domain: string): Promise<PublicTenantBranding> {
    const response = await client.get<PublicTenantBranding>(`/branding/by-domain/${domain}`);
    return response.data;
  },
};

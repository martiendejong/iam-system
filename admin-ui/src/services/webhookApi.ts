import { api } from './api';
import type {
  WebhookSubscription,
  WebhookDelivery,
  EventTypesResponse,
  CreateWebhookRequest,
  UpdateWebhookRequest,
} from '../types/webhooks';

class WebhookApiService {
  async getWebhooks(tenantId: string): Promise<WebhookSubscription[]> {
    const response = await api.getClient().get(`/webhooks?tenantId=${tenantId}`);
    return response.data as WebhookSubscription[];
  }

  async getWebhook(id: string): Promise<WebhookSubscription> {
    const response = await api.getClient().get(`/webhooks/${id}`);
    return response.data as WebhookSubscription;
  }

  async createWebhook(data: CreateWebhookRequest): Promise<WebhookSubscription> {
    const response = await api.getClient().post('/webhooks', data);
    return response.data as WebhookSubscription;
  }

  async updateWebhook(id: string, data: UpdateWebhookRequest): Promise<WebhookSubscription> {
    const response = await api.getClient().put(`/webhooks/${id}`, data);
    return response.data as WebhookSubscription;
  }

  async deleteWebhook(id: string): Promise<void> {
    await api.getClient().delete(`/webhooks/${id}`);
  }

  async testWebhook(id: string): Promise<WebhookDelivery> {
    const response = await api.getClient().post(`/webhooks/${id}/test`);
    return response.data as WebhookDelivery;
  }

  async getDeliveries(id: string, limit = 50): Promise<WebhookDelivery[]> {
    const response = await api.getClient().get(`/webhooks/${id}/deliveries?limit=${limit}`);
    return response.data as WebhookDelivery[];
  }

  async getEventTypes(): Promise<EventTypesResponse> {
    const response = await api.getClient().get('/webhooks/event-types');
    return response.data as EventTypesResponse;
  }
}

export const webhookApi = new WebhookApiService();

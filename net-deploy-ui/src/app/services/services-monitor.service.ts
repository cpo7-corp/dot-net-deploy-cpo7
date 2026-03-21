import { Injectable } from '@angular/core';
import { ApiService } from './api.service';
import { ServiceDefinition, ServiceHeartbeatStatus, ServiceStatus } from '../models/api-models';
import { Observable } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class ServicesMonitorService extends ApiService {

  getAll(): Observable<ServiceStatus[]> {
    return this.http.get<ServiceStatus[]>(`${this.baseUrl}/services`);
  }

  getHeartbeats(environmentId: string): Observable<ServiceHeartbeatStatus[]> {
    return this.http.get<ServiceHeartbeatStatus[]>(`${this.baseUrl}/services/heartbeats`, {
      params: { environmentId }
    });
  }

  create(service: ServiceDefinition): Observable<ServiceDefinition> {
    return this.http.post<ServiceDefinition>(`${this.baseUrl}/services`, service);
  }

  update(id: string, service: ServiceDefinition): Observable<ServiceDefinition> {
    return this.http.put<ServiceDefinition>(`${this.baseUrl}/services/${id}`, service);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/services/${id}`);
  }

  reorder(ids: string[]): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/services/reorder`, ids);
  }
}

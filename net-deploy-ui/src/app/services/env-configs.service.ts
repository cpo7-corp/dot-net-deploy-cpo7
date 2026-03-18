import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiService } from './api.service';
import { EnvConfigSet } from '../models/api-models';

@Injectable({
  providedIn: 'root'
})
export class EnvConfigsService extends ApiService {
  private endpoint = `${this.baseUrl}/env-configs`;

  getAll(): Observable<EnvConfigSet[]> {
    return this.http.get<EnvConfigSet[]>(this.endpoint);
  }

  getById(id: string): Observable<EnvConfigSet> {
    return this.http.get<EnvConfigSet>(`${this.endpoint}/${id}`);
  }

  create(data: EnvConfigSet): Observable<EnvConfigSet> {
    return this.http.post<EnvConfigSet>(this.endpoint, data);
  }

  update(id: string, data: EnvConfigSet): Observable<EnvConfigSet> {
    return this.http.put<EnvConfigSet>(`${this.endpoint}/${id}`, data);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.endpoint}/${id}`);
  }
}

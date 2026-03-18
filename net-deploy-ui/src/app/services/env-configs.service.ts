import { Injectable } from '@angular/core';
import { ApiService } from './api.service';
import { EnvConfigSet } from '../models/api-models';
import { firstValueFrom } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class EnvConfigsService extends ApiService {

  async getAll(): Promise<EnvConfigSet[]> {
    return firstValueFrom(this.http.get<EnvConfigSet[]>(`${this.baseUrl}/EnvConfigs`));
  }

  async getById(id: string): Promise<EnvConfigSet> {
    return firstValueFrom(this.http.get<EnvConfigSet>(`${this.baseUrl}/EnvConfigs/${id}`));
  }

  async create(configSet: EnvConfigSet): Promise<EnvConfigSet> {
    return firstValueFrom(this.http.post<EnvConfigSet>(`${this.baseUrl}/EnvConfigs`, configSet));
  }

  async update(id: string, configSet: EnvConfigSet): Promise<EnvConfigSet> {
    return firstValueFrom(this.http.put<EnvConfigSet>(`${this.baseUrl}/EnvConfigs/${id}`, configSet));
  }

  async delete(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.baseUrl}/EnvConfigs/${id}`));
  }
}

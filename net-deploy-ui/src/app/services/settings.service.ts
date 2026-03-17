import { Injectable } from '@angular/core';
import { ApiService } from './api.service';
import { AppSettings } from '../models/api-models';
import { Observable } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class SettingsService extends ApiService {
  
  getSettings(): Observable<AppSettings> {
    return this.http.get<AppSettings>(`${this.baseUrl}/settings`);
  }

  saveSettings(settings: AppSettings): Observable<AppSettings> {
    return this.http.put<AppSettings>(`${this.baseUrl}/settings`, settings);
  }
}

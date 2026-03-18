import { Injectable } from '@angular/core';
import { ApiService } from './api.service';
import { Observable } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class MaintenanceService extends ApiService {
  
  exportDatabase(): Observable<any> {
    return this.http.get(`${this.baseUrl}/maintenance/export`);
  }

  importDatabase(data: any): Observable<any> {
    return this.http.post(`${this.baseUrl}/maintenance/import`, data);
  }
}

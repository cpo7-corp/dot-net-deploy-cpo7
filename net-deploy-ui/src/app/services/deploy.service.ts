import { Injectable, NgZone } from '@angular/core';
import { ApiService } from './api.service';
import { DeployLogEntry } from '../models/api-models';
import { Observable, Subject } from 'rxjs';
import { HttpClient } from '@angular/common/http';

@Injectable({
  providedIn: 'root'
})
export class DeployService extends ApiService {

  constructor(private zone: NgZone, http: HttpClient) {
    super();
    this.http = http;
  }

  /**
   * Triggers a deploy and returns an Observable that streams log entries
   * via Server-Sent Events (SSE).
   */
  deploy(services: { serviceId: string, branch?: string }[], environmentId?: string | null, forceClean: boolean = false, cloneAllFirst: boolean = false, skipBuildIfOutputExists: boolean = false): Observable<DeployLogEntry> {
    return this.streamLogs(`${this.baseUrl}/deploy`, { services, environmentId, forceClean, cloneAllFirst, skipBuildIfOutputExists });
  }

  serviceAction(serviceId: string, environmentId: string, action: string): Observable<DeployLogEntry> {
    return this.streamLogs(`${this.baseUrl}/deploy/service-action`, { serviceId, environmentId, action });
  }

  private streamLogs(url: string, body: any): Observable<DeployLogEntry> {
    const subject = new Subject<DeployLogEntry>();

    fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    }).then(async response => {
      if (!response.body) throw new Error('No body returned from server.');
      
      const reader = response.body.getReader();
      const decoder = new TextDecoder('utf-8');
      
      let buffer = '';
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n\n');
        buffer = lines.pop() || '';

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const dataStr = line.replace('data: ', '').trim();
            if (dataStr) {
              const entry = JSON.parse(dataStr) as DeployLogEntry;
              this.zone.run(() => {
                subject.next(entry);
                if (entry.level === 'DONE') subject.complete();
              });
            }
          }
        }
      }
    }).catch(err => {
      this.zone.run(() => subject.error(err));
    });

    return subject.asObservable();
  }


  getSessions(count = 10): Observable<string[]> {
    return this.http!.get<string[]>(`${this.baseUrl}/Deploy/sessions?count=${count}`);
  }

  getSessionsPaged(skip = 0, limit = 20): Observable<SessionSummary[]> {
    return this.http!.get<SessionSummary[]>(`${this.baseUrl}/Deploy/sessions-paged?skip=${skip}&limit=${limit}`);
  }

  getLogs(sessionId: string): Observable<DeployLogEntry[]> {
    return this.http!.get<DeployLogEntry[]>(`${this.baseUrl}/Deploy/logs/${sessionId}`);
  }
}

export interface SessionSummary {
  sessionId: string;
  timestamp: string;
  hasErrors: boolean;
}

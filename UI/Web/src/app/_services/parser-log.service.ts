import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { environment } from '../../environments/environment';

/**
 * Fork-only service for the Parser Logs page. Kept separate from
 * `library.service.ts` so upstream's surface stays untouched.
 */
@Injectable({ providedIn: 'root' })
export class ParserLogService {
  private readonly httpClient = inject(HttpClient);
  private readonly baseUrl = environment.apiUrl;

  getParserDebugLog(libraryId: number = 0, limit: number = 500) {
    return this.httpClient.get<ParserDebugEntry[]>(
      `${this.baseUrl}parser-log?libraryId=${libraryId}&limit=${limit}`);
  }

  clearParserDebugLog() {
    return this.httpClient.post(`${this.baseUrl}parser-log/clear`, {});
  }
}

export enum ParserOutcome {
  Accepted = 0,
  Lenient = 1,
  Rejected = 2,
}

export interface ParserDebugEntry {
  timestampUtc: string;
  libraryId: number;
  /** Library strictness level (1 or 2) at the time of parsing. */
  levelAtParse: number;
  filePath: string;
  libraryRoot: string;
  outcome: ParserOutcome;
  series: string;
  volumes: string;
  chapters: string;
  isSpecial: boolean;
  seriesReleaseYear: number;
  tokens: string;
  reason: string;
}

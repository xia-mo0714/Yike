/* Build-time only: compile against the pinned, hash-checked v1.9.4 headers.
   The probe is not shipped and does not load or call Whisper. */
#include "whisper.h"
#include <stdio.h>
#define FIELD(T,N) printf("\"%s\":{\"offset\":%zu,\"size\":%zu},",#N,offsetof(T,N),sizeof(((T*)0)->N))
#define BEGIN(T,N) printf("\"%s\":{\"size\":%zu,\"fields\":{",N,sizeof(T))
#define END(T) printf("\"_end\":{\"offset\":%zu,\"size\":0}}}",sizeof(T))
int main(void){
 printf("{\"pointerSize\":%zu,\"boolSize\":%zu,\"structures\":{",sizeof(void*),sizeof(bool));
 BEGIN(struct whisper_context_params,"ContextParams");
 FIELD(struct whisper_context_params,use_gpu);FIELD(struct whisper_context_params,flash_attn);FIELD(struct whisper_context_params,gpu_device);
 FIELD(struct whisper_context_params,dtw_token_timestamps);FIELD(struct whisper_context_params,dtw_aheads_preset);FIELD(struct whisper_context_params,dtw_n_top);
 FIELD(struct whisper_context_params,dtw_aheads);FIELD(struct whisper_context_params,dtw_mem_size);END(struct whisper_context_params);printf(",");
 BEGIN(struct whisper_full_params,"FullParams");
 FIELD(struct whisper_full_params,strategy);FIELD(struct whisper_full_params,n_threads);FIELD(struct whisper_full_params,n_max_text_ctx);
 FIELD(struct whisper_full_params,offset_ms);FIELD(struct whisper_full_params,duration_ms);FIELD(struct whisper_full_params,translate);
 FIELD(struct whisper_full_params,no_context);FIELD(struct whisper_full_params,no_timestamps);FIELD(struct whisper_full_params,single_segment);
 FIELD(struct whisper_full_params,print_special);FIELD(struct whisper_full_params,print_progress);FIELD(struct whisper_full_params,print_realtime);
 FIELD(struct whisper_full_params,print_timestamps);FIELD(struct whisper_full_params,token_timestamps);FIELD(struct whisper_full_params,thold_pt);
 FIELD(struct whisper_full_params,thold_ptsum);FIELD(struct whisper_full_params,max_len);FIELD(struct whisper_full_params,split_on_word);
 FIELD(struct whisper_full_params,max_tokens);FIELD(struct whisper_full_params,debug_mode);FIELD(struct whisper_full_params,audio_ctx);
 FIELD(struct whisper_full_params,tdrz_enable);FIELD(struct whisper_full_params,suppress_regex);FIELD(struct whisper_full_params,initial_prompt);
 FIELD(struct whisper_full_params,carry_initial_prompt);FIELD(struct whisper_full_params,prompt_tokens);FIELD(struct whisper_full_params,prompt_n_tokens);
 FIELD(struct whisper_full_params,language);FIELD(struct whisper_full_params,detect_language);FIELD(struct whisper_full_params,suppress_blank);
 FIELD(struct whisper_full_params,suppress_nst);FIELD(struct whisper_full_params,temperature);FIELD(struct whisper_full_params,max_initial_ts);
 FIELD(struct whisper_full_params,length_penalty);FIELD(struct whisper_full_params,temperature_inc);FIELD(struct whisper_full_params,entropy_thold);
 FIELD(struct whisper_full_params,logprob_thold);FIELD(struct whisper_full_params,no_speech_thold);FIELD(struct whisper_full_params,greedy);
 FIELD(struct whisper_full_params,beam_search);FIELD(struct whisper_full_params,new_segment_callback);FIELD(struct whisper_full_params,new_segment_callback_user_data);
 FIELD(struct whisper_full_params,progress_callback);FIELD(struct whisper_full_params,progress_callback_user_data);FIELD(struct whisper_full_params,encoder_begin_callback);
 FIELD(struct whisper_full_params,encoder_begin_callback_user_data);FIELD(struct whisper_full_params,abort_callback);FIELD(struct whisper_full_params,abort_callback_user_data);
 FIELD(struct whisper_full_params,logits_filter_callback);FIELD(struct whisper_full_params,logits_filter_callback_user_data);FIELD(struct whisper_full_params,grammar_rules);
 FIELD(struct whisper_full_params,n_grammar_rules);FIELD(struct whisper_full_params,i_start_rule);FIELD(struct whisper_full_params,grammar_penalty);
 FIELD(struct whisper_full_params,vad);FIELD(struct whisper_full_params,vad_model_path);FIELD(struct whisper_full_params,vad_params);END(struct whisper_full_params);printf(",");
 BEGIN(whisper_vad_params,"VadParams");FIELD(whisper_vad_params,threshold);FIELD(whisper_vad_params,min_speech_duration_ms);
 FIELD(whisper_vad_params,min_silence_duration_ms);FIELD(whisper_vad_params,max_speech_duration_s);FIELD(whisper_vad_params,speech_pad_ms);
 FIELD(whisper_vad_params,samples_overlap);END(whisper_vad_params);printf(",");
 BEGIN(whisper_token_data,"TokenData");FIELD(whisper_token_data,id);FIELD(whisper_token_data,tid);FIELD(whisper_token_data,p);FIELD(whisper_token_data,plog);
 FIELD(whisper_token_data,pt);FIELD(whisper_token_data,ptsum);FIELD(whisper_token_data,t0);FIELD(whisper_token_data,t1);
 FIELD(whisper_token_data,t_dtw);FIELD(whisper_token_data,vlen);END(whisper_token_data);printf("}}\n");return 0;
}
